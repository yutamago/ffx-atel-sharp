// SPDX-License-Identifier: MIT
//
// Writes src/Ffx.Atel.Game/Members.g.cs: the object-oriented surface over the natives (docs/language.md,
// "Members"), from data/native-members.json, data/natives.json and the native docs of data/native-types.json.
//
//   - A member of the worker (`Pos`, `Yaw`, `EnableTrigger(…)`) is a `protected static` member of Worker: there
//     is one instance of each worker, and requested entries are static.
//   - A component (`Turn`, `Move`, `Motion`, `Neck`) is a property of Worker whose class holds the members.
//   - An indexed group (`Sprites`) is a property of Worker with an indexer; the index is the native's first
//     argument.
//   - A static group (`Wait`, `Camera`, `Sound`) is a static class of namespace Ffx; a nested static group
//     (`Camera.Eye`, its name the parent's and its own) is a static class inside its parent's: `Camera.Eye.AimAt(…)`.
//   - A native whose first argument is a worker ("worker": true) is reached unqualified (this worker), through
//     the worker's class (`Tidus.Turn.Stop()`) or through `Workers[n]` (WorkerHandle mirrors those members).
//
// Each member carries [Atel.NativeMember(id)] on the method or the property accessor: the compiler lowers a use
// onto that native (Ffx.Atel.Compiler.NativeMembers), the decompiler writes the member where the bytecode is that
// native's call. Doc comments are the natives' own.
//
// usage: dotnet run tools/gen-members.cs

#:property PublishAot=false

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// The checkout: this file's folder's parent, or (where the build maps source paths, Directory.Build.props) the
// working directory.
string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, ".."));
if (!File.Exists(Path.Combine(root, "data", "natives.json"))) root = Directory.GetCurrentDirectory();
string Data(string name) => File.ReadAllText(Path.Combine(root, "data", name));

// ----- Inputs ---------------------------------------------------------------------------------------------
Dictionary<ushort, Native> natives = [];
using (JsonDocument doc = JsonDocument.Parse(Data("natives.json"))) {
    foreach (JsonElement n in doc.RootElement.GetProperty("natives").EnumerateArray()) {
        ushort id = Hex(n.GetProperty("id").GetString()!);
        List<Arg> nativeArgs = [.. n.GetProperty("args").EnumerateArray().Select(a => new Arg(
            a.GetProperty("name").GetString()!, a.GetProperty("kind").GetString()!,
            a.TryGetProperty("type", out JsonElement t) ? t.GetString() : null))];
        natives[id] = new Native(id, n.GetProperty("class").GetString()!, n.GetProperty("method").GetString()!, nativeArgs,
            n.GetProperty("result").GetString()!, n.TryGetProperty("result_type", out JsonElement rt) ? rt.GetString() : null,
            n.GetProperty("polls").GetBoolean(), n.GetProperty("doc").GetString() ?? "", n.GetProperty("calls").GetInt64());
    }
}
Dictionary<ushort, (string? Summary, Dictionary<string, string> Params)> hand = [];
using (JsonDocument doc = JsonDocument.Parse(Data("native-types.json"))) {
    foreach (JsonProperty p in doc.RootElement.GetProperty("docs").EnumerateObject()) {
        JsonElement e = p.Value;
        hand[Hex(p.Name)] = (e.TryGetProperty("summary", out JsonElement s) ? s.GetString() : null,
            e.TryGetProperty("params", out JsonElement ps) ? ps.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!) : []);
    }
}
Dictionary<string, Group> groups = [];
List<Spec> specs = [];
using (JsonDocument doc = JsonDocument.Parse(Data("native-members.json"))) {
    foreach (JsonProperty g in doc.RootElement.GetProperty("groups").EnumerateObject()) {
        JsonElement e = g.Value;
        string kind = e.GetProperty("kind").GetString()!;
        int dot = g.Name.LastIndexOf('.');
        string? parent = dot < 0 ? null : g.Name[..dot];
        if (parent is not null && (kind != "static" || !groups.TryGetValue(parent, out Group? p) || p.Kind != "static")) {
            throw new InvalidOperationException($"group {g.Name}: a nested group is static, in a static group listed before it");
        }
        groups[g.Name] = new Group(g.Name, kind, e.TryGetProperty("type", out JsonElement t) ? t.GetString()! : g.Name[(dot + 1)..],
            e.TryGetProperty("item", out JsonElement it) ? it.GetString() : null, e.GetProperty("summary").GetString()!, parent);
    }
    foreach (JsonProperty m in doc.RootElement.GetProperty("members").EnumerateObject()) {
        JsonElement e = m.Value;
        ushort id = Hex(m.Name);
        if (!natives.ContainsKey(id)) throw new InvalidOperationException($"native-members.json: no native {m.Name}");
        specs.Add(new Spec(id, e.GetProperty("member").GetString()!, e.TryGetProperty("as", out JsonElement a) ? a.GetString()! : "method",
            Flag(e, "worker"), Flag(e, "vector"), Flag(e, "bool")));
    }
}
static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.GetBoolean();

// ----- Containers and their members -----------------------------------------------------------------------
// "" is the worker itself; a group's name is its container.
Dictionary<string, Container> containers = new() { [""] = new Container("") };
foreach (Group g in groups.Values) containers[g.Name] = new Container(g.Name);
HashSet<string> vectorRoots = [];
foreach (Spec s in specs) {
    string[] path = s.Member.Split('.');
    // The group the member is in: its path but the last part (Camera.Eye for Camera.Eye.AimAt), when that is one.
    string? group = path.Length >= 2 && groups.ContainsKey(string.Join('.', path[..^1])) ? string.Join('.', path[..^1]) : null;
    if (group is null && path.Length > 2) throw new InvalidOperationException($"{s.Member}: no group {string.Join('.', path[..^1])}");
    Native n = natives[s.Id];
    bool indexed = group is not null && groups[group].Kind == "indexed";
    int receivers = s.Worker || indexed ? 1 : 0;
    if (s.Worker && indexed) throw new InvalidOperationException($"{s.Member}: a worker and an index");
    if (receivers > 0 && (n.Args.Count == 0 || n.Args[0].Kind != "int")) throw new InvalidOperationException($"0x{s.Id:x4}: the receiver is the first int argument");
    List<Arg> rest = [.. n.Args.Skip(receivers)];
    if (groups.TryGetValue(s.Member, out Group? root0)) {
        // The group's own property: a component root that takes an assignment (Motion = 0x71050).
        if (root0.Kind != "component" || path.Length != 1 || s.As != "set") throw new InvalidOperationException($"{s.Member}: only a component takes a value");
        containers[""].Assign(root0, s, n, rest);
        continue;
    }
    if (path.Length == 2 && group is null) {
        // A component of a Vector3 property of the worker: Pos.X.
        if (s.As != "get" || path[1] is not ("X" or "Y" or "Z")) throw new InvalidOperationException($"{s.Member}: a vector component is a getter X, Y or Z");
        if (rest.Count != 0 || n.Result != "float") throw new InvalidOperationException($"0x{s.Id:x4}: a vector component takes no value and returns a float");
        vectorRoots.Add(path[0]);
        containers[""].Prop(path[0], "Vector3").Add(new Accessor("get", s, n, path[1]));
        continue;
    }
    Container c = containers[group ?? ""];
    string name = path[^1];
    switch (s.As) {
        case "method":
            c.Method(name, s, n, rest);
            break;
        case "set": {
            string type;
            if (s.Vector) {
                if (rest.Count != 3 || rest.Any(a => a.Kind != "float")) throw new InvalidOperationException($"0x{s.Id:x4}: a Vector3 setter takes three floats");
                type = "Vector3";
            }
            else {
                if (rest.Count != 1) throw new InvalidOperationException($"0x{s.Id:x4}: a setter takes one value ({rest.Count})");
                type = s.Bool ? "bool" : CsType(rest[0]);
            }
            c.Prop(name, type).Add(new Accessor("set", s, n, null));
            break;
        }
        case "get":
            if (rest.Count != 0) throw new InvalidOperationException($"0x{s.Id:x4}: a getter takes no value");
            c.Prop(name, ResultType(n)).Add(new Accessor("get", s, n, null));
            break;
        default:
            throw new InvalidOperationException($"{s.Member}: \"as\" is method, get or set");
    }
}

// ----- Output ---------------------------------------------------------------------------------------------
StringBuilder cs = new();
cs.Append("""
    // SPDX-License-Identifier: MIT
    // <auto-generated> by tools/gen-members.cs from data/native-members.json: do not edit. </auto-generated>

    #nullable enable

    """);
int memberCount = 0, nativeCount = specs.Count;

// The worker's own members, components, indexed groups and Workers.
cs.Append("""

    namespace Ffx.Atel
    {
        public abstract partial class Worker
        {
            /// <summary>
            ///     Another worker, by number: <c>Workers[n].Turn.Stop()</c> acts on worker <c>n</c> (its position in
            ///     <c>[Workers]</c>). A worker of the script is reached through its class as well, <c>Tidus.Turn.Stop()</c>.
            /// </summary>
            protected static WorkerList Workers => throw Runtime.NotRunnable();

    """);
foreach (Group g in groups.Values.Where(g => g.Kind is "component" or "indexed")) {
    Container own = containers[""];
    cs.Append(CultureInfo.InvariantCulture, $"\n        /// <summary>{g.Summary}</summary>\n");
    if (own.Assigns.TryGetValue(g.Name, out var assign)) {
        cs.Append(Remarks("        ", [(assign.Spec, assign.Native, "set")]));
        cs.Append(CultureInfo.InvariantCulture, $"        protected static {g.Type} {g.Name} {{ get => throw Runtime.NotRunnable(); {Attr(assign.Spec, assign.Native, null, "")}set => throw Runtime.NotRunnable(); }}\n");
    }
    else {
        cs.Append(CultureInfo.InvariantCulture, $"        protected static {g.Type} {g.Name} => throw Runtime.NotRunnable();\n");
    }
    memberCount++;
}
WriteMembers(containers[""], "        ", "protected static ", "");
cs.Append("    }\n}\n\nnamespace Ffx\n{\n");

// Vector3.
// (hand-written: src/Ffx.Atel.Game/Vector3.cs)

// Components and indexed items.
foreach (Group g in groups.Values) {
    Container c = containers[g.Name];
    switch (g.Kind) {
        case "component": {
            cs.Append(CultureInfo.InvariantCulture, $"\n    /// <summary>{g.Summary}</summary>\n");
            cs.Append(CultureInfo.InvariantCulture, $"    /// <remarks>The worker's <c>{g.Name}</c> property (<c>{g.Name}.…</c>; another worker's: <c>Tidus.{g.Name}.…</c>, <c>Workers[n].{g.Name}.…</c>).</remarks>\n");
            cs.Append(CultureInfo.InvariantCulture, $"    public sealed partial class {g.Type}\n    {{\n        private {g.Type}() {{ }}\n");
            if (containers[""].Assigns.TryGetValue(g.Name, out var assign)) {
                string valueType = CsType(assign.Values[0]);
                cs.Append(CultureInfo.InvariantCulture, $"\n        /// <summary>Assigning a value to the worker's <c>{g.Name}</c> calls <c>{assign.Native.Class}.{assign.Native.Method}</c>: <c>{g.Name} = …;</c>.</summary>\n");
                cs.Append(CultureInfo.InvariantCulture, $"        public static implicit operator {g.Type}({valueType} {assign.Values[0].Name}) => throw Atel.Runtime.NotRunnable();\n");
            }
            WriteMembers(c, "        ", "public ", "Atel.");
            cs.Append("    }\n");
            break;
        }
        case "indexed": {
            cs.Append(CultureInfo.InvariantCulture, $"\n    /// <summary>{g.Summary}</summary>\n");
            cs.Append(CultureInfo.InvariantCulture, $"    public sealed partial class {g.Type}\n    {{\n        private {g.Type}() {{ }}\n\n");
            string index = c.Methods.Values.SelectMany(m => m).Select(m => m.Native.Args[0].Name).FirstOrDefault() ?? "index";
            cs.Append(CultureInfo.InvariantCulture, $"        /// <summary>The one at <paramref name=\"{index}\"/>.</summary>\n");
            cs.Append(CultureInfo.InvariantCulture, $"        public {g.Item} this[int {index}] => throw Atel.Runtime.NotRunnable();\n    }}\n");
            cs.Append(CultureInfo.InvariantCulture, $"\n    /// <summary>One of <c>{g.Name}</c>: <c>{g.Name}[{index}].…</c>.</summary>\n");
            cs.Append(CultureInfo.InvariantCulture, $"    public sealed class {g.Item}\n    {{\n        private {g.Item}() {{ }}\n");
            WriteMembers(c, "        ", "public ", "Atel.");
            cs.Append("    }\n");
            break;
        }
        case "static":
            // A nested group is written inside its parent's class.
            if (g.Parent is null) WriteStatic(g, "    ");
            break;
        default:
            throw new InvalidOperationException($"group {g.Name}: kind is component, indexed or static");
    }
}

// WorkerHandle: the members that reach another worker, and the components that hold some.
{
    Container own = containers[""];
    Container mirror = new("WorkerHandle");
    foreach ((string name, Prop p) in own.Props) if (p.Accessors.Any(a => a.Spec.Worker)) mirror.Props[name] = p;
    foreach ((string name, List<MethodDecl> ms) in own.Methods) if (ms.Any(m => m.Spec.Worker)) mirror.Methods[name] = ms;
    cs.Append("""

        /// <summary>Another worker: <c>Workers[n]</c>. Holds the members that act on a given worker.</summary>
        public sealed class WorkerHandle
        {
            private WorkerHandle() { }

        """);
    foreach (Group g in groups.Values.Where(g => g.Kind == "component" && containers[g.Name].All.Any(a => a.Worker))) {
        cs.Append(CultureInfo.InvariantCulture, $"\n        /// <summary>{g.Summary}</summary>\n        public {g.Type} {g.Name} => throw Atel.Runtime.NotRunnable();\n");
    }
    WriteMembers(mirror, "        ", "public ", "Atel.", count: false);
    cs.Append("""
        }

        /// <summary>The script's workers by number: <c>Workers[n]</c>.</summary>
        public sealed class WorkerList
        {
            private WorkerList() { }

            /// <summary>Worker <paramref name="worker"/>, its position in <c>[Workers]</c>.</summary>
            public WorkerHandle this[int worker] => throw Atel.Runtime.NotRunnable();
        }

        """);
}
cs.Append("}\n");

File.WriteAllText(Path.Combine(root, "src", "Ffx.Atel.Game", "Members.g.cs"), cs.ToString());
long covered = specs.Sum(s => natives[s.Id].Calls), total = natives.Values.Sum(n => n.Calls);
Console.WriteLine($"{nativeCount} natives as {memberCount} members; {covered} of {total} calls in the shipped scripts ({100.0 * covered / total:F1} %)");
return 0;

// ----- Writing members -------------------------------------------------------------------------------------
// A static group's class, its members and then its nested groups' classes.
void WriteStatic(Group g, string indent) {
    cs.Append(CultureInfo.InvariantCulture, $"\n{indent}/// <summary>{g.Summary}</summary>\n");
    cs.Append(CultureInfo.InvariantCulture, $"{indent}public static partial class {g.Type}\n{indent}{{\n");
    WriteMembers(containers[g.Name], indent + "    ", "public static ", "Atel.");
    foreach (Group nested in groups.Values.Where(x => x.Parent == g.Name)) {
        if (containers[g.Name].Props.ContainsKey(nested.Type) || containers[g.Name].Methods.ContainsKey(nested.Type)) {
            throw new InvalidOperationException($"group {nested.Name}: {g.Name} has a member {nested.Type}");
        }
        memberCount++;
        WriteStatic(nested, indent + "    ");
    }
    cs.Append(CultureInfo.InvariantCulture, $"{indent}}}\n");
}

void WriteMembers(Container c, string indent, string modifiers, string atel, bool count = true) {
    foreach ((string name, Prop p) in c.Props) {
        if (count) memberCount++;
        Accessor? get = p.Accessors.FirstOrDefault(a => a.Kind == "get");
        Accessor? set = p.Accessors.FirstOrDefault(a => a.Kind == "set");
        Accessor main = set ?? get!;
        string summary = Summary(main.Spec, main.Native, propertyOf: true);
        if (vectorRoots.Contains(name) && c.Name == "") summary += " Its <c>X</c>, <c>Y</c> and <c>Z</c> read the worker's position.";
        cs.Append(CultureInfo.InvariantCulture, $"\n{indent}/// <summary>{summary}</summary>\n");
        cs.Append(Remarks(indent, [.. p.Accessors.Select(a => (a.Spec, a.Native, a.Component is null ? a.Kind : $"{a.Kind} {a.Component}"))]));
        StringBuilder accessors = new();
        foreach (string kind in new[] { "get", "set" }) {
            List<Accessor> of = [.. p.Accessors.Where(a => a.Kind == kind)];
            // A Vector3 property that is only set still needs a getter in C# for nothing; a getter only for X/Y/Z.
            if (of.Count == 0) continue;
            accessors.Append(' ');
            foreach (Accessor a in of) accessors.Append(Attr(a.Spec, a.Native, a.Component, atel));
            accessors.Append(CultureInfo.InvariantCulture, $"{kind} => throw {atel}Runtime.NotRunnable();");
        }
        cs.Append(CultureInfo.InvariantCulture, $"{indent}{modifiers}{p.Type} {name} {{{accessors} }}\n");
    }
    foreach ((string name, List<MethodDecl> ms) in c.Methods) {
        foreach (IGrouping<string, MethodDecl> overload in ms.GroupBy(m => string.Join(",", m.Params.Select(CsType)))) {
            if (count) memberCount++;
            MethodDecl first = overload.First();
            // The method's parameters: the native's without the receiver; a self and a worker native share them.
            List<Arg> ps = first.Params;
            string ret = overload.Select(m => ResultType(m.Native, method: true)).Distinct().Single();
            HashSet<string> names = [.. ps.Select(a => a.Name)];
            cs.Append(CultureInfo.InvariantCulture, $"\n{indent}/// <summary>{Summary(first.Spec, first.Native, names: names)}</summary>\n");
            foreach (Arg a in ps) {
                string text = hand.TryGetValue(first.Native.Id, out var h) && h.Params.TryGetValue(a.Name, out string? d) ? Rewrite(d, first.Native, names) : "";
                cs.Append(CultureInfo.InvariantCulture, $"{indent}/// <param name=\"{a.Name}\">{text}</param>\n");
            }
            cs.Append(Remarks(indent, [.. overload.Select(m => (m.Spec, m.Native, "call"))]));
            foreach (MethodDecl m in overload) cs.Append(CultureInfo.InvariantCulture, $"{indent}{Attr(m.Spec, m.Native, null, atel)}\n");
            cs.Append(CultureInfo.InvariantCulture, $"{indent}{modifiers}{ret} {name}({string.Join(", ", ps.Select(a => $"{CsType(a)} {a.Name}"))}) => throw {atel}Runtime.NotRunnable();\n");
        }
    }
}

string Attr(Spec s, Native n, string? component, string atel) {
    StringBuilder a = new();
    a.Append(CultureInfo.InvariantCulture, $"[{atel}NativeMember(0x{n.Id:x4}");
    if (n.Polls) a.Append(", Polls = true");
    if (s.Worker) a.Append(", Worker = true");
    if (component is not null) a.Append(CultureInfo.InvariantCulture, $", Component = \"{component}\"");
    return a.Append(")] ").ToString().TrimEnd() + " ";
}

string Remarks(string indent, IReadOnlyList<(Spec Spec, Native Native, string What)> uses) {
    StringBuilder r = new();
    foreach ((Spec s, Native n, string what) in uses) {
        string how = what switch {
            "call" => "Calls",
            "set" => "Setting it calls",
            _ => $"Reading {(what.StartsWith("get ", StringComparison.Ordinal) ? $"<c>{what[4..]}</c>" : "it")} calls",
        };
        r.Append(CultureInfo.InvariantCulture, $"{how} native <c>0x{n.Id:x4}</c>, <c>{n.Class}.{n.Method}</c> ({n.Calls} calls in the shipped scripts)");
        if (s.Worker) {
            bool selfToo = uses.Any(u => !u.Spec.Worker && u.What == what);
            r.Append(selfToo
                ? " on another worker, written before it (<c>Tidus.…</c>, <c>Workers[n].…</c>)"
                : ", with the worker: this one (<c>Common.GetWorkerId(-1)</c>) when written without a worker, else the one written before it (<c>Tidus.…</c>, <c>Workers[n].…</c>)");
        }
        r.Append(". ");
    }
    return $"{indent}/// <remarks>{r.ToString().TrimEnd()}</remarks>\n";
}

/// <summary>The native's summary for a member: the hand-written one, else the reverse-engineering note.</summary>
string Summary(Spec s, Native n, bool propertyOf = false, HashSet<string>? names = null) {
    string text = hand.TryGetValue(n.Id, out var h) && h.Summary is string summary
        ? summary
        : n.Doc.Length > 0 ? System.Security.SecurityElement.Escape(n.Doc)! : "Not yet described.";
    return Rewrite(text, n, propertyOf ? [] : names ?? []);
}

/// <summary>
///     A native's doc text in another type: a <c>paramref</c> to a parameter the member does not have becomes
///     <c>c</c>, and a <c>cref</c> to a member of the native's class names the class.
/// </summary>
static string Rewrite(string text, Native n, HashSet<string> names) {
    text = Regex.Replace(text, "<paramref name=\"(\\w+)\"\\s*/>", m => names.Contains(m.Groups[1].Value) ? m.Value : $"<c>{m.Groups[1].Value}</c>");
    return Regex.Replace(text, "cref=\"(\\w+)\"", m => $"cref=\"{n.Class}.{m.Groups[1].Value}\"");
}

static string CsType(Arg a) => a.Type ?? (a.Kind == "float" ? "float" : "int");

static string ResultType(Native n, bool method = false) => n.Result switch {
    "float" => "float",
    "none" when method => "void",
    "none" => throw new InvalidOperationException($"0x{n.Id:x4} returns nothing: no getter"),
    _ => n.ResultType == "bool" ? "bool" : "int",
};

static ushort Hex(string s) => ushort.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

static string ThisFile([CallerFilePath] string path = "") => path;

internal sealed record Arg(string Name, string Kind, string? Type);

internal sealed record Native(ushort Id, string Class, string Method, List<Arg> Args, string Result, string? ResultType, bool Polls, string Doc, long Calls);

/// <param name="Name">The group's path: <c>Turn</c>, <c>Camera</c>, <c>Camera.Eye</c> (nested in <c>Camera</c>).</param>
/// <param name="Type">The class's name: the component's (<c>TurnControl</c>), else the path's last part.</param>
/// <param name="Parent">The static group a nested group is in.</param>
internal sealed record Group(string Name, string Kind, string Type, string? Item, string Summary, string? Parent);

internal sealed record Spec(ushort Id, string Member, string As, bool Worker, bool Vector, bool Bool);

internal sealed record Accessor(string Kind, Spec Spec, Native Native, string? Component);

internal sealed record MethodDecl(Spec Spec, Native Native, List<Arg> Params);

internal sealed class Prop(string type) {
    public string Type { get; } = type;
    public List<Accessor> Accessors { get; } = [];

    public void Add(Accessor a) {
        if (Accessors.Any(x => x.Kind == a.Kind && x.Spec.Worker == a.Spec.Worker && x.Component == a.Component)) {
            throw new InvalidOperationException($"{a.Spec.Member}: two {a.Kind} natives{(a.Spec.Worker ? " with a worker" : "")}");
        }
        Accessors.Add(a);
    }
}

internal sealed class Container(string name) {
    public string Name { get; } = name;
    public Dictionary<string, Prop> Props { get; } = [];
    public Dictionary<string, List<MethodDecl>> Methods { get; } = [];
    public Dictionary<string, (Spec Spec, Native Native, List<Arg> Values)> Assigns { get; } = [];

    public IEnumerable<Spec> All => Props.Values.SelectMany(p => p.Accessors.Select(a => a.Spec)).Concat(Methods.Values.SelectMany(m => m.Select(x => x.Spec)));

    public Prop Prop(string name, string type) {
        if (Methods.ContainsKey(name)) throw new InvalidOperationException($"{name}: a method and a property");
        if (!Props.TryGetValue(name, out Prop? p)) Props[name] = p = new Prop(type);
        if (p.Type != type) throw new InvalidOperationException($"{name}: {p.Type} and {type}");
        return p;
    }

    public void Method(string name, Spec s, Native n, List<Arg> rest) {
        if (Props.ContainsKey(name)) throw new InvalidOperationException($"{name}: a method and a property");
        if (!Methods.TryGetValue(name, out List<MethodDecl>? list)) Methods[name] = list = [];
        list.Add(new MethodDecl(s, n, rest));
    }

    public void Assign(Group g, Spec s, Native n, List<Arg> rest) {
        if (rest.Count != 1) throw new InvalidOperationException($"{s.Member}: an assignment takes one value");
        Assigns[g.Name] = (s, n, rest);
    }
}
