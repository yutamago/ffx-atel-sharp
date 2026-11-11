// SPDX-License-Identifier: MIT
//
// Writes the game library API pages of the website (docs/website.md, "Reference"): one Markdown page per public
// type of Ffx.Atel.Game, and one per namespace listing its types, into site/src/content/docs/reference/api/. The
// text is the assembly's XML doc comments; the signatures come from the assembly itself. The pages are build
// output (.gitignore): the Pages workflow runs this before `astro build`. The hand-written landing page is
// site/src/content/docs/reference/api.md.
//
//   - A page per type: its declaration, summary and remarks, then its members by kind (constructors, properties,
//     methods, operators, fields), overloads under one heading. Enums, and classes of many constants (Story,
//     Battle), list their values in a table.
//   - The remarks the generators write for natives (the native's id, its call count, the reverse-engineering
//     note) and the summaries that are still such a note go into a collapsed "Native details" block: a modder
//     reads the plain text first.
//   - <see cref="…"/> links to the type's page and the member's heading; the anchors are computed as Astro's
//     heading ids (github-slugger), so starlight-links-validator checks them.
//   - The type pages are hidden from the sidebar (sidebar.hidden): the landing page and the namespace pages
//     lead to them, and the sidebar stays short.
//
// usage: dotnet run tools/gen-api-pages.cs [-- <output dir>]

#:project ../src/Ffx.Atel.Game
#:property PublishAot=false

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

const string SiteBase = "/ffx-atel-sharp/reference/api";

string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, ".."));
if (!Directory.Exists(Path.Combine(root, "site"))) root = Directory.GetCurrentDirectory();
string outDir = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "site", "src", "content", "docs", "reference", "api");

Assembly asm = typeof(Ffx.Wait).Assembly;
string xmlPath = Path.ChangeExtension(asm.Location, ".xml");
if (!File.Exists(xmlPath)) throw new FileNotFoundException($"no XML docs beside {asm.Location} (GenerateDocumentationFile)", xmlPath);
Dictionary<string, XElement> docs = [];
foreach (XElement m in XDocument.Load(xmlPath).Root!.Element("members")!.Elements("member")) docs[m.Attribute("name")!.Value] = m;

List<Type> types = [.. asm.GetExportedTypes().Where(t => !t.IsDefined(typeof(CompilerGeneratedAttribute)))
    .OrderBy(t => t.Namespace, StringComparer.Ordinal).ThenBy(t => DisplayName(t), StringComparer.Ordinal)];
Dictionary<Type, TypePage> pages = [];
foreach (Type t in types) pages[t] = new TypePage(t, NamespaceSlug(t.Namespace!), TypeSlug(t));
foreach (IGrouping<string, TypePage> dup in pages.Values.GroupBy(p => $"{p.Ns}/{p.Slug}").Where(g => g.Count() > 1)) {
    throw new InvalidOperationException($"two types on one page {dup.Key}: {string.Join(", ", dup.Select(p => p.Type.FullName))}");
}

// ----- Members, sections and anchors (first pass, so that links can point at any page) ---------------------------
const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
Dictionary<string, (TypePage Page, string Anchor)> memberLinks = [];
foreach (TypePage page in pages.Values) {
    Type t = page.Type;
    Slugger slugger = new();
    page.Sections = [];
    if (t.IsEnum) {
        page.ValueTable = [.. t.GetFields(BindingFlags.Public | BindingFlags.Static).OrderBy(f => f.MetadataToken)];
        slugger.Slug("Values");
        page.Sections.Add(new Section("Values", []));
    }
    else {
        List<MemberInfo> members = [.. t.GetMembers(Declared).Where(Visible).OrderBy(m => m.MetadataToken)];
        List<FieldInfo> constants = [.. members.OfType<FieldInfo>().Where(f => f.IsLiteral)];
        // Many constants (Story, Battle, Cut): a table reads better than a heading per value.
        if (constants.Count > 30) {
            page.ValueTable = constants;
            members = [.. members.Except(constants)];
        }
        void Add(string title, IEnumerable<MemberInfo> of, Func<MemberInfo, string> heading) {
            List<IGrouping<string, MemberInfo>> groups = [.. of.GroupBy(heading)];
            if (groups.Count == 0) return;
            slugger.Slug(title);
            Section s = new(title, []);
            foreach (IGrouping<string, MemberInfo> g in groups) {
                string anchor = slugger.Slug(g.Key);
                s.Groups.Add(new MemberGroup(g.Key, anchor, [.. g]));
                foreach (MemberInfo m in g) memberLinks[DocId(m)] = (page, anchor);
            }
            page.Sections.Add(s);
        }
        // An implicit default constructor (no parameters, no comment) says nothing: left out.
        Add("Constructors", members.OfType<ConstructorInfo>().Where(c => c.GetParameters().Length > 0 || docs.ContainsKey(DocId(c))), _ => DisplayName(t));
        Add("Properties", members.OfType<PropertyInfo>(), m => ((PropertyInfo)m).GetIndexParameters().Length > 0 ? "this[]" : m.Name);
        Add("Methods", members.OfType<MethodInfo>().Where(m => !m.IsSpecialName), m => m.Name);
        Add("Operators", members.OfType<MethodInfo>().Where(m => m.IsSpecialName && m.Name.StartsWith("op_", StringComparison.Ordinal)), m => OperatorHeading((MethodInfo)m));
        Add("Fields", members.OfType<FieldInfo>(), m => m.Name);
        Add("Events", members.OfType<EventInfo>(), m => m.Name);
        if (page.ValueTable is not null) {
            slugger.Slug("Constants");
            page.Sections.Add(new Section("Constants", []));
        }
    }
    if (page.ValueTable is not null) {
        foreach (FieldInfo f in page.ValueTable) memberLinks[DocId(f)] = (page, page.Sections[^1].Title.ToLowerInvariant());
    }
}

// ----- Writing ---------------------------------------------------------------------------------------------------
if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
int undocumented = 0;
foreach (TypePage page in pages.Values) {
    StringBuilder md = new();
    Type t = page.Type;
    XElement? doc = docs.GetValueOrDefault(DocId(t));
    (string summary, string? notes) = SummaryOf(doc, t);
    md.Append("---\n");
    md.Append(CultureInfo.InvariantCulture, $"title: {Yaml(DisplayName(t))}\n");
    md.Append(CultureInfo.InvariantCulture, $"description: {Yaml(Truncate(PlainFirstSentence(summary.Length > 0 ? summary : $"The {Kind(t)} {DisplayName(t)} of the Atel# game library."), 160))}\n");
    md.Append("sidebar:\n  hidden: true\n");
    int memberCount = page.Sections.Sum(s => s.Groups.Count);
    if (memberCount > 60) md.Append("tableOfContents:\n  maxHeadingLevel: 2\n");
    md.Append("---\n\n");
    md.Append(CultureInfo.InvariantCulture, $"{Capitalize(Kind(t))} in namespace [{t.Namespace}]({SiteBase}/{page.Ns}/)");
    if (t.DeclaringType is Type outer && pages.ContainsKey(outer)) md.Append(CultureInfo.InvariantCulture, $", inside [{DisplayName(outer)}]({PageUrl(pages[outer])})");
    if (BaseOf(t) is Type b) md.Append(CultureInfo.InvariantCulture, $". Derives from {TypeLink(b, t)}");
    md.Append(".\n\n");
    md.Append("```csharp\n").Append(Declaration(t)).Append("\n```\n\n");
    if (summary.Length > 0) md.Append(summary).Append("\n\n");
    else md.Append("Not described yet.\n\n");
    AppendDetails(md, Remarks(doc, t), notes);
    AppendSeeAlso(md, doc, t);
    if (t.IsSubclassOf(typeof(Ffx.Atel.Worker)) || t == typeof(Ffx.Atel.Worker)) {
        if (t != typeof(Ffx.Atel.Worker)) md.Append(CultureInfo.InvariantCulture, $"The members every worker has (its position, turn, move, motion and the rest) are on the page of {TypeLink(typeof(Ffx.Atel.Worker), t)}.\n\n");
    }

    foreach (Section s in page.Sections) {
        md.Append(CultureInfo.InvariantCulture, $"## {s.Title}\n\n");
        if (s.Title is "Values" or "Constants") {
            AppendValueTable(md, page);
            continue;
        }
        foreach (MemberGroup g in s.Groups) {
            md.Append(CultureInfo.InvariantCulture, $"### {g.Heading}\n\n");
            bool many = g.Members.Count > 1;
            foreach (MemberInfo m in g.Members) {
                XElement? mdoc = docs.GetValueOrDefault(DocId(m));
                md.Append("```csharp\n").Append(Signature(m)).Append("\n```\n\n");
                (string msum, string? mnotes) = SummaryOf(mdoc, t);
                if (msum.Length > 0) md.Append(msum).Append("\n\n");
                else if (mdoc is null || mnotes is not null) {
                    md.Append("Not described yet.\n\n");
                    undocumented++;
                }
                AppendParams(md, m, mdoc, t);
                if (mdoc?.Element("example") is XElement ex && Block(ex, t) is { Length: > 0 } extext) md.Append("**Example**\n\n").Append(extext).Append("\n\n");
                if (mdoc?.Element("returns") is XElement ret && Inline(ret, t) is { Length: > 0 } rtext) md.Append(CultureInfo.InvariantCulture, $"**Returns** {rtext}\n\n");
                AppendDetails(md, Remarks(mdoc, t), mnotes);
                AppendSeeAlso(md, mdoc, t);
                if (many && m != g.Members[^1]) md.Append("---\n\n");
            }
        }
    }
    string file = Path.Combine(outDir, page.Ns, page.Slug + ".md");
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.WriteAllText(file, md.ToString().TrimEnd() + "\n");
}

// The namespace pages: the types by kind, with the first sentence of each summary.
foreach (IGrouping<string, TypePage> ns in pages.Values.GroupBy(p => p.Type.Namespace!)) {
    StringBuilder md = new();
    md.Append("---\n");
    md.Append(CultureInfo.InvariantCulture, $"title: {Yaml($"Namespace {ns.Key}")}\n");
    md.Append(CultureInfo.InvariantCulture, $"description: {Yaml($"Every public type of the namespace {ns.Key} in the Atel# game library.")}\n");
    md.Append("sidebar:\n  hidden: true\n");
    md.Append("---\n\n");
    md.Append(ns.Key == "Ffx"
        ? "The game's API: the natives grouped by what they act on, the worker members, the enums of game ids, the save fields and the story values.\n\n"
        : "What Atel source is made of: the worker base classes, the attributes a mod and the decompiled source use, and the task types behind requests.\n\n");
    md.Append(CultureInfo.InvariantCulture, $"Back to the [game library API](/ffx-atel-sharp/reference/api/).\n\n");
    foreach (IGrouping<string, TypePage> kind in ns.GroupBy(p => KindGroup(p.Type)).OrderBy(g => KindOrder(g.Key))) {
        md.Append(CultureInfo.InvariantCulture, $"## {kind.Key}\n\n| Type | What it is |\n|---|---|\n");
        foreach (TypePage p in kind) {
            (string s, _) = SummaryOf(docs.GetValueOrDefault(DocId(p.Type)), p.Type);
            md.Append(CultureInfo.InvariantCulture, $"| [`{DisplayName(p.Type)}`]({PageUrl(p)}) | {Cell(FirstSentence(s))} |\n");
        }
        md.Append('\n');
    }
    string file = Path.Combine(outDir, NamespaceSlug(ns.Key), "index.md");
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.WriteAllText(file, md.ToString().TrimEnd() + "\n");
}

// A doc comment no page shows: a member that is not public (fine), or a doc id this tool computes otherwise than
// the compiler (a bug here). Listed, so that a reader can tell.
HashSet<string> shown = [.. pages.Keys.Select(t => DocId(t)), .. memberLinks.Keys];
List<string> unshown = [.. docs.Keys.Where(k => !shown.Contains(k))];
foreach (string k in unshown.Take(20)) Console.WriteLine($"  not on a page: {k}");
Console.WriteLine($"{pages.Count} type pages in {outDir}; {memberLinks.Count} members, {undocumented} not described yet; {unshown.Count} doc comments not on a page");
return 0;

// ----- Doc text --------------------------------------------------------------------------------------------------

// The summary as Markdown, and the research note when the summary is one: the natives nobody has described yet
// carry the reverse-engineering note as their summary ("0x0002: args (kind 0-3, …) -> 0; …").
(string Summary, string? Notes) SummaryOf(XElement? doc, Type context) {
    List<XElement> summaries = doc?.Elements("summary").ToList() ?? [];
    if (summaries.Count == 0) return ("", null);
    // A partial type with a hand-written summary and a generated one ("Natives of group 7 (0x7000–)."): the
    // hand-written one is the summary, the generated one goes to the notes.
    List<string> texts = [.. summaries.Select(s => Block(s, context)).Where(s => s.Length > 0)];
    List<string> plain = [.. texts.Where(s => !IsNote(s))], notes = [.. texts.Where(IsNote)];
    string? note = notes.Count > 0 ? (plain.Count == 0 ? "Research notes: " : "") + string.Join("\n\n", notes) : null;
    return (string.Join("\n\n", plain), note);
}

static bool IsNote(string text) => Regex.IsMatch(text, @"^\??(\\\?)?0x[0-9a-f]{4}\b") || text.StartsWith("Natives of group", StringComparison.Ordinal)
    || text == "Not yet described.";

string? Remarks(XElement? doc, Type context) => doc?.Element("remarks") is XElement r && Block(r, context) is { Length: > 0 } text ? text : null;

// The remarks and the research notes, collapsed: the summary is what a reader needs first; the remarks hold the
// fine print (the rules at the edges, the game's own names, offsets) and, for natives, the native's id and call
// count.
static void AppendDetails(StringBuilder md, string? remarks, string? notes) {
    if (remarks is null && notes is null) return;
    md.Append("<details>\n<summary>Details</summary>\n\n");
    if (remarks is not null) md.Append(remarks).Append("\n\n");
    if (notes is not null) md.Append(notes).Append("\n\n");
    md.Append("</details>\n\n");
}

void AppendSeeAlso(StringBuilder md, XElement? doc, Type context) {
    List<string> links = [.. (doc?.Elements("seealso") ?? []).Select(e => Inline(new XElement("x", e), context)).Where(s => s.Length > 0)];
    if (links.Count > 0) md.Append("See also: ").Append(string.Join(", ", links)).Append(".\n\n");
}

void AppendParams(StringBuilder md, MemberInfo m, XElement? doc, Type context) {
    ParameterInfo[] ps = m switch {
        MethodBase mb => mb.GetParameters(),
        PropertyInfo p => p.GetIndexParameters(),
        _ => [],
    };
    List<string> lines = [];
    foreach (ParameterInfo p in ps) {
        string text = doc?.Elements("param").FirstOrDefault(e => e.Attribute("name")?.Value == p.Name) is XElement e ? Inline(e, context) : "";
        if (text.Length > 0) lines.Add($"- `{p.Name}`: {text}");
    }
    foreach (XElement tp in doc?.Elements("typeparam") ?? []) {
        string text = Inline(tp, context);
        if (text.Length > 0) lines.Add($"- `{tp.Attribute("name")?.Value}`: {text}");
    }
    if (lines.Count > 0) md.Append("**Parameters**\n\n").Append(string.Join("\n", lines)).Append("\n\n");
}

void AppendValueTable(StringBuilder md, TypePage page) {
    List<FieldInfo> fields = page.ValueTable!;
    List<long> values = [.. fields.Select(f => Convert.ToInt64(f.GetRawConstantValue(), CultureInfo.InvariantCulture))];
    bool hex = page.Type.IsDefined(typeof(FlagsAttribute)) || values.Any(v => v >= 0x1000 || v < -0x1000);
    md.Append("| Name | Value | What it is |\n|---|---|---|\n");
    for (int i = 0; i < fields.Count; i++) {
        long v = values[i];
        string value = hex ? (v < 0 ? $"-0x{-v:x}" : $"0x{v:x}") : v.ToString(CultureInfo.InvariantCulture);
        (string s, string? n) = SummaryOf(docs.GetValueOrDefault(DocId(fields[i])), page.Type);
        string text = s.Length > 0 ? s : n is not null ? "Not described yet." : "";
        md.Append(CultureInfo.InvariantCulture, $"| `{fields[i].Name}` | `{value}` | {Cell(text)} |\n");
    }
    md.Append('\n');
}

// XML doc content as Markdown blocks: <para> and <code> make paragraphs, the rest is inline.
string Block(XElement e, Type context) {
    List<string> blocks = [];
    StringBuilder current = new();
    void Flush() {
        string s = Regex.Replace(current.ToString(), @"\s+", " ").Trim();
        if (s.Length > 0) blocks.Add(s);
        current.Clear();
    }
    foreach (XNode n in e.Nodes()) {
        if (n is XElement { Name.LocalName: "para" } p) {
            Flush();
            blocks.Add(Block(p, context));
        }
        else if (n is XElement { Name.LocalName: "code" } c) {
            Flush();
            blocks.Add("```csharp\n" + Dedent(c.Value) + "\n```");
        }
        else if (n is XElement { Name.LocalName: "list" } l) {
            Flush();
            blocks.Add(string.Join("\n", l.Elements("item").Select(i => "- " + Inline(i.Element("description") ?? i, context))));
        }
        else if (n is XElement { Name.LocalName: "seealso" }) {
        }
        else current.Append(InlineNode(n, context));
    }
    Flush();
    return string.Join("\n\n", blocks.Where(b => b.Length > 0));
}

string Inline(XElement e, Type context) => Regex.Replace(string.Concat(e.Nodes().Select(n => InlineNode(n, context))), @"\s+", " ").Trim();

string InlineNode(XNode n, Type context) => n switch {
    XText text => DocsLinks(Escape(text.Value)),
    XElement { Name.LocalName: "c" } c => Code(c.Value),
    XElement { Name.LocalName: "paramref" or "typeparamref" } p => Code(p.Attribute("name")?.Value ?? ""),
    XElement { Name.LocalName: "see" or "seealso" } s when s.Attribute("langword") is XAttribute w => Code(w.Value),
    XElement { Name.LocalName: "see" or "seealso" } s when s.Attribute("href") is XAttribute h => $"[{(s.Value.Length > 0 ? Escape(s.Value) : Escape(h.Value))}]({h.Value})",
    XElement { Name.LocalName: "see" or "seealso" } s when s.Attribute("cref") is XAttribute r => CrefLink(r.Value, s.Value, context),
    XElement { Name.LocalName: "br" } => " ",
    XElement { Name.LocalName: "para" } p => " " + Inline(p, context) + " ",
    XElement other => Inline(other, context),
    _ => "",
};

string CrefLink(string cref, string text, Type context) {
    string label = text.Length > 0 ? text : CrefLabel(cref, context);
    if (cref.StartsWith("T:", StringComparison.Ordinal)) {
        TypePage? p = pages.Values.FirstOrDefault(x => DocId(x.Type) == cref);
        return p is null ? Code(label) : $"[{Code(label)}]({PageUrl(p)})";
    }
    if (memberLinks.TryGetValue(cref, out var link)) return $"[{Code(label)}]({PageUrl(link.Page)}#{link.Anchor})";
    return Code(label);
}

// The name a cref shows: the member's, with its type's when the type is another one.
string CrefLabel(string cref, Type context) {
    string id = cref[2..];
    int paren = id.IndexOf('(', StringComparison.Ordinal);
    if (paren >= 0) id = id[..paren];
    int tilde = id.IndexOf('~', StringComparison.Ordinal);
    if (tilde >= 0) id = id[..tilde];
    id = Regex.Replace(id, "`+\\d+", "");
    if (cref.StartsWith("T:", StringComparison.Ordinal)) return ShortTypeName(id);
    int dot = id.LastIndexOf('.');
    string typeName = id[..dot], member = id[(dot + 1)..];
    if (member == "#ctor") member = ShortTypeName(typeName);
    string contextName = Regex.Replace((context.FullName ?? "").Replace('+', '.'), "`\\d+", "");
    return typeName == contextName ? member : $"{ShortTypeName(typeName)}.{member}";
}

string ShortTypeName(string fullName) {
    TypePage? p = pages.Values.FirstOrDefault(x => Regex.Replace(x.Type.FullName!.Replace('+', '.'), "`\\d+", "") == fullName);
    return p is not null ? DisplayName(p.Type) : fullName[(fullName.LastIndexOf('.') + 1)..];
}

// "docs/research/frame-timing.md" in a comment: a link to the file on GitHub, as the site's "Details" links; only
// for a file of this repository (not "ffx-rust docs/formats/battle.md").
string DocsLinks(string md) => Regex.Replace(md, @"(?<![\w/])docs/[\w/.-]*?\w\.md(?=$|[^\w/])",
    m => File.Exists(Path.Combine(root, m.Value)) ? $"[{m.Value}](https://github.com/yutamago/ffx-atel-sharp/blob/main/{m.Value})" : m.Value);

static string Code(string s) {
    s = Regex.Replace(s, @"\s+", " ").Trim();
    if (s.Length == 0) return "";
    string fence = s.Contains('`', StringComparison.Ordinal) ? "``" : "`";
    string pad = s.StartsWith('`') || s.EndsWith('`') ? " " : "";
    return $"{fence}{pad}{s}{pad}{fence}";
}

static string Escape(string s) {
    StringBuilder sb = new(s.Length);
    foreach (char c in s) {
        sb.Append(c switch {
            '\\' => "\\\\",
            '`' => "\\`",
            '*' => "\\*",
            '_' => "\\_",
            '[' => "\\[",
            ']' => "\\]",
            '<' => "&lt;",
            '>' => "&gt;",
            '&' => "&amp;",
            '{' => "&#123;",
            '}' => "&#125;",
            _ => c.ToString(),
        });
    }
    return sb.ToString();
}

static string Dedent(string code) {
    List<string> lines = [.. code.Replace("\r", "", StringComparison.Ordinal).Split('\n')];
    while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
    while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
    int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
    return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()));
}

// The first sentence of Markdown, its code spans and links kept: for the type lists.
static string FirstSentence(string md) {
    string first = md.Split("\n\n")[0];
    bool code = false;
    for (int i = 0; i < first.Length - 1; i++) {
        if (first[i] == '`') code = !code;
        else if (!code && first[i] is '.' or '!' or '?' && first[i + 1] == ' ' && i + 2 < first.Length && (char.IsUpper(first[i + 2]) || first[i + 2] is '(' or '[' or '`')) return first[..(i + 1)];
    }
    return first;
}

// Plain text of Markdown: the first sentence, for the pages' descriptions.
static string PlainFirstSentence(string md) {
    string first = md.Split("\n\n")[0];
    string plain = Regex.Replace(first, @"\[(`?)([^\]]*?)\1\]\([^)]*\)", "$2");
    plain = plain.Replace("\\", "", StringComparison.Ordinal);
    plain = Regex.Replace(plain, "`+", "");
    plain = plain.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal)
        .Replace("&#123;", "{", StringComparison.Ordinal).Replace("&#125;", "}", StringComparison.Ordinal);
    Match m = Regex.Match(plain, @"^.*?[.!?](?=\s+[A-Z(]|$)");
    return (m.Success ? m.Value : plain).Trim();
}

static string Truncate(string s, int max) => s.Length <= max ? s : s[..s.LastIndexOf(' ', max - 1)].TrimEnd(',', ';', ':') + " …";

static string Cell(string md) => md.Replace("\n", " ", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);

static string Yaml(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

// ----- Names, kinds and signatures -------------------------------------------------------------------------------

static bool Visible(MemberInfo m) {
    if (m.IsDefined(typeof(CompilerGeneratedAttribute))) return false;
    return m switch {
        MethodBase mb => (mb.IsPublic || mb.IsFamily || mb.IsFamilyOrAssembly) && !(mb.IsSpecialName && (mb.Name.StartsWith("get_", StringComparison.Ordinal)
            || mb.Name.StartsWith("set_", StringComparison.Ordinal) || mb.Name.StartsWith("add_", StringComparison.Ordinal) || mb.Name.StartsWith("remove_", StringComparison.Ordinal)))
            && !(mb is ConstructorInfo && mb.IsStatic) && mb.Name != "Finalize",
        PropertyInfo p => (p.GetMethod is MethodInfo g && AccessorVisible(g)) || (p.SetMethod is MethodInfo s && AccessorVisible(s)),
        FieldInfo f => (f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly) && !f.IsSpecialName,
        EventInfo e => e.AddMethod is MethodInfo a && (a.IsPublic || a.IsFamily),
        _ => false,
    };
}

static bool AccessorVisible(MethodInfo accessor) => accessor.IsPublic || accessor.IsFamily || accessor.IsFamilyOrAssembly;

static string NamespaceSlug(string ns) => ns.ToLowerInvariant().Replace('.', '-');

static string TypeSlug(Type t) {
    string name = t.Name.Replace('`', '-').ToLowerInvariant();
    return t.DeclaringType is Type outer ? $"{TypeSlug(outer)}-{name}" : name;
}

static string PageUrl(TypePage p) => $"{SiteBase}/{p.Ns}/{p.Slug}/";

static string DisplayName(Type t) {
    string name = t.Name;
    int tick = name.IndexOf('`', StringComparison.Ordinal);
    if (tick >= 0) name = name[..tick];
    Type[] own = t.GetGenericArguments().Skip(t.DeclaringType?.GetGenericArguments().Length ?? 0).ToArray();
    if (own.Length > 0) name += "<" + string.Join(", ", own.Select(a => a.Name)) + ">";
    return t.DeclaringType is Type outer ? $"{DisplayName(outer)}.{name}" : name;
}

static string Kind(Type t) =>
    t.IsEnum ? "enum"
    : t.IsValueType ? (t.IsDefined(typeof(IsReadOnlyAttribute)) ? "readonly struct" : "struct")
    : t.IsInterface ? "interface"
    : typeof(Delegate).IsAssignableFrom(t) ? "delegate"
    : t.IsAbstract && t.IsSealed ? "static class"
    : typeof(Attribute).IsAssignableFrom(t) ? "attribute"
    : t.IsAbstract ? "abstract class"
    : "class";

static string KindGroup(Type t) =>
    t.IsEnum ? "Enums"
    : t.IsValueType ? "Structs"
    : typeof(Attribute).IsAssignableFrom(t) ? "Attributes"
    : t.IsInterface ? "Interfaces"
    : t.IsAbstract && t.IsSealed ? "Static classes"
    : "Classes";

static int KindOrder(string group) => Array.IndexOf(new[] { "Static classes", "Classes", "Structs", "Enums", "Attributes", "Interfaces" }, group);

static Type? BaseOf(Type t) => t.BaseType is Type b && b != typeof(object) && b != typeof(ValueType) && b != typeof(Enum) && !t.IsEnum ? b : null;

string TypeLink(Type t, Type context) {
    if (t.IsGenericType && !t.IsGenericTypeDefinition && pages.TryGetValue(t.GetGenericTypeDefinition(), out TypePage? gp)) return $"[{Code(CsName(t))}]({PageUrl(gp)})";
    return pages.TryGetValue(t, out TypePage? p) ? $"[{Code(DisplayName(t))}]({PageUrl(p)})" : Code(CsName(t));
}

static string Declaration(Type t) {
    StringBuilder sb = new("public ");
    if (t.IsEnum) {
        sb.Append("enum ").Append(DisplayName(t));
        Type u = Enum.GetUnderlyingType(t);
        if (u != typeof(int)) sb.Append(" : ").Append(CsName(u));
        return sb.ToString();
    }
    if (t.IsValueType) sb.Append(t.IsDefined(typeof(IsReadOnlyAttribute)) ? "readonly struct " : "struct ");
    else if (t.IsInterface) sb.Append("interface ");
    else {
        if (t.IsAbstract && t.IsSealed) sb.Append("static ");
        else if (t.IsAbstract) sb.Append("abstract ");
        else if (t.IsSealed) sb.Append("sealed ");
        sb.Append("class ");
    }
    sb.Append(DisplayName(t)[(t.DeclaringType is null ? 0 : DisplayName(t.DeclaringType).Length + 1)..]);
    List<string> bases = [];
    if (BaseOf(t) is Type b && !(t.IsAbstract && t.IsSealed)) bases.Add(CsName(b));
    bases.AddRange(t.GetInterfaces().Where(i => t.BaseType is null || !i.IsAssignableFrom(t.BaseType)).Select(CsName));
    if (bases.Count > 0) sb.Append(" : ").Append(string.Join(", ", bases));
    return sb.ToString();
}

static string CsName(Type t) {
    if (t.IsByRef) return CsName(t.GetElementType()!);
    if (t.IsArray) return CsName(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
    if (t.IsGenericParameter) return t.Name;
    if (Nullable.GetUnderlyingType(t) is Type n) return CsName(n) + "?";
    string? alias = t.FullName switch {
        "System.Void" => "void", "System.Int32" => "int", "System.UInt32" => "uint", "System.Int16" => "short", "System.UInt16" => "ushort",
        "System.Byte" => "byte", "System.SByte" => "sbyte", "System.Int64" => "long", "System.UInt64" => "ulong", "System.Single" => "float",
        "System.Double" => "double", "System.Boolean" => "bool", "System.String" => "string", "System.Object" => "object", "System.Char" => "char",
        _ => null,
    };
    if (alias is not null) return alias;
    string name = t.Name;
    int tick = name.IndexOf('`', StringComparison.Ordinal);
    if (tick >= 0) name = name[..tick];
    Type[] args = t.GetGenericArguments().Skip(t.DeclaringType?.GetGenericArguments().Length ?? 0).ToArray();
    if (args.Length > 0) name += "<" + string.Join(", ", args.Select(CsName)) + ">";
    return t.DeclaringType is Type outer && !t.IsGenericParameter ? $"{CsName(outer)}.{name}" : name;
}

static string Modifiers(MethodBase m) {
    StringBuilder sb = new(m.IsPublic ? "public " : "protected ");
    if (m.IsStatic) sb.Append("static ");
    else if (m.IsAbstract && !m.DeclaringType!.IsInterface) sb.Append("abstract ");
    else if (m.IsVirtual && !m.IsFinal && m is MethodInfo mi && mi.GetBaseDefinition() == mi) sb.Append("virtual ");
    else if (m.IsVirtual && m is MethodInfo mo && mo.GetBaseDefinition() != mo) sb.Append("override ");
    return sb.ToString();
}

static string Params(ParameterInfo[] ps) => string.Join(", ", ps.Select(Param));

static string Param(ParameterInfo p) {
    StringBuilder sb = new();
    foreach (CustomAttributeData a in p.CustomAttributes.Where(a => a.AttributeType.Namespace == "Ffx.Atel")) {
        string name = a.AttributeType.Name[..^"Attribute".Length];
        List<string> argv = [.. a.ConstructorArguments.Select(Literal)];
        argv.AddRange(a.NamedArguments.Select(n => $"{n.MemberName} = {Literal(n.TypedValue)}"));
        sb.Append('[').Append(name).Append(argv.Count > 0 ? $"({string.Join(", ", argv)})" : "").Append("] ");
    }
    if (p.IsDefined(typeof(ParamArrayAttribute))) sb.Append("params ");
    if (p.ParameterType.IsByRef) sb.Append(p.IsOut ? "out " : p.IsIn ? "in " : "ref ");
    sb.Append(CsName(p.ParameterType)).Append(' ').Append(p.Name);
    if (p.HasDefaultValue) sb.Append(" = ").Append(p.DefaultValue switch {
        null => "default",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        float f => f.ToString(CultureInfo.InvariantCulture) + "f",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        object o => o.ToString(),
    });
    return sb.ToString();
}

static string Literal(CustomAttributeTypedArgument a) => a.Value switch {
    Type t => $"typeof({CsName(t)})",
    string s => $"\"{s}\"",
    bool b => b ? "true" : "false",
    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
    null => "null",
    object o => o.ToString() ?? "",
};

static string Accessors(PropertyInfo p) {
    StringBuilder sb = new("{ ");
    bool pub = (p.GetMethod?.IsPublic ?? false) || (p.SetMethod?.IsPublic ?? false);
    foreach (MethodInfo? a in new[] { p.GetMethod, p.SetMethod }) {
        if (a is null || !(a.IsPublic || a.IsFamily || a.IsFamilyOrAssembly)) continue;
        if (pub && !a.IsPublic) sb.Append("protected ");
        bool init = a == p.SetMethod && a.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
        sb.Append(a == p.GetMethod ? "get; " : init ? "init; " : "set; ");
    }
    return sb.Append('}').ToString();
}

static string Signature(MemberInfo m) {
    switch (m) {
        case ConstructorInfo c:
            return $"{(c.IsPublic ? "public" : "protected")} {DisplayName(c.DeclaringType!).Split('.')[^1].Split('<')[0]}({Params(c.GetParameters())})";
        case MethodInfo mi when mi.IsSpecialName && mi.Name is "op_Implicit" or "op_Explicit":
            return $"public static {(mi.Name == "op_Implicit" ? "implicit" : "explicit")} operator {CsName(mi.ReturnType)}({Params(mi.GetParameters())})";
        case MethodInfo mi when mi.IsSpecialName:
            return $"public static {CsName(mi.ReturnType)} operator {OperatorSymbol(mi.Name)}({Params(mi.GetParameters())})";
        case MethodInfo mi: {
            string generic = mi.IsGenericMethodDefinition ? "<" + string.Join(", ", mi.GetGenericArguments().Select(a => a.Name)) + ">" : "";
            string async = mi.IsDefined(typeof(AsyncStateMachineAttribute)) ? "async " : "";
            string ext = mi.IsDefined(typeof(ExtensionAttribute)) ? "this " : "";
            return $"{Modifiers(mi)}{async}{CsName(mi.ReturnType)} {mi.Name}{generic}({ext}{Params(mi.GetParameters())})";
        }
        case PropertyInfo p: {
            MethodInfo a = (p.GetMethod is MethodInfo g && (g.IsPublic || g.IsFamily) ? g : p.SetMethod)!;
            string name = p.GetIndexParameters().Length > 0 ? $"this[{Params(p.GetIndexParameters())}]" : p.Name;
            string vis = (p.GetMethod?.IsPublic ?? false) || (p.SetMethod?.IsPublic ?? false) ? "public " : "protected ";
            string mods = Modifiers(a)[(a.IsPublic ? "public ".Length : "protected ".Length)..];
            return $"{vis}{mods}{CsName(p.PropertyType)} {name} {Accessors(p)}";
        }
        case FieldInfo f: {
            string vis = f.IsPublic ? "public " : "protected ";
            if (f.IsLiteral) {
                object? v = f.GetRawConstantValue();
                string value = v switch {
                    string s => $"\"{s}\"",
                    float x => x.ToString(CultureInfo.InvariantCulture) + "f",
                    bool b => b ? "true" : "false",
                    IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
                    _ => v?.ToString() ?? "null",
                };
                return $"{vis}const {CsName(f.FieldType)} {f.Name} = {value};";
            }
            return $"{vis}{(f.IsStatic ? "static " : "")}{(f.IsInitOnly ? "readonly " : "")}{CsName(f.FieldType)} {f.Name};";
        }
        case EventInfo e:
            return $"public event {CsName(e.EventHandlerType!)} {e.Name};";
        default:
            return m.Name;
    }
}

static string OperatorHeading(MethodInfo m) => m.Name switch {
    "op_Implicit" => $"implicit operator {CsName(m.ReturnType)}",
    "op_Explicit" => $"explicit operator {CsName(m.ReturnType)}",
    _ => $"operator {OperatorSymbol(m.Name)}",
};

static string OperatorSymbol(string name) => name switch {
    "op_Addition" => "+", "op_Subtraction" => "-", "op_Multiply" => "*", "op_Division" => "/", "op_Modulus" => "%",
    "op_Equality" => "==", "op_Inequality" => "!=", "op_LessThan" => "<", "op_GreaterThan" => ">",
    "op_LessThanOrEqual" => "<=", "op_GreaterThanOrEqual" => ">=", "op_UnaryNegation" => "-", "op_UnaryPlus" => "+",
    "op_LogicalNot" => "!", "op_BitwiseAnd" => "&", "op_BitwiseOr" => "|", "op_ExclusiveOr" => "^", "op_OnesComplement" => "~",
    "op_True" => "true", "op_False" => "false", "op_Increment" => "++", "op_Decrement" => "--",
    _ => name,
};

// ----- XML doc ids (ECMA-334 annex D) ----------------------------------------------------------------------------

static string DocId(MemberInfo m) => m switch {
    Type t => "T:" + IdTypeName(t),
    ConstructorInfo c => $"M:{IdTypeName(c.DeclaringType!)}.#ctor{IdParams(c.GetParameters())}",
    MethodInfo mi => $"M:{IdTypeName(mi.DeclaringType!)}.{mi.Name}{(mi.IsGenericMethodDefinition ? "``" + mi.GetGenericArguments().Length : "")}{IdParams(mi.GetParameters())}"
        + (mi.Name is "op_Implicit" or "op_Explicit" ? "~" + IdParamType(mi.ReturnType) : ""),
    PropertyInfo p => $"P:{IdTypeName(p.DeclaringType!)}.{p.Name}{IdParams(p.GetIndexParameters())}",
    FieldInfo f => $"F:{IdTypeName(f.DeclaringType!)}.{f.Name}",
    EventInfo e => $"E:{IdTypeName(e.DeclaringType!)}.{e.Name}",
    _ => m.Name,
};

static string IdTypeName(Type t) => t.DeclaringType is Type outer ? $"{IdTypeName(outer)}.{t.Name}" : $"{t.Namespace}.{t.Name}";

static string IdParams(ParameterInfo[] ps) => ps.Length == 0 ? "" : "(" + string.Join(",", ps.Select(p => IdParamType(p.ParameterType))) + ")";

static string IdParamType(Type t) {
    if (t.IsByRef) return IdParamType(t.GetElementType()!) + "@";
    if (t.IsPointer) return IdParamType(t.GetElementType()!) + "*";
    if (t.IsArray) return IdParamType(t.GetElementType()!) + (t.GetArrayRank() == 1 ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", t.GetArrayRank())) + "]");
    if (t.IsGenericParameter) return (t.DeclaringMethod is null ? "`" : "``") + t.GenericParameterPosition;
    if (t.IsGenericType) {
        string def = IdTypeName(t.GetGenericTypeDefinition());
        def = def[..def.LastIndexOf('`')];
        return def + "{" + string.Join(",", t.GetGenericArguments().Select(IdParamType)) + "}";
    }
    return IdTypeName(t);
}

static string ThisFile([CallerFilePath] string path = "") => path;

internal sealed record TypePage(Type Type, string Ns, string Slug) {
    public List<Section> Sections { get; set; } = [];

    public List<FieldInfo>? ValueTable { get; set; }
}

internal sealed record Section(string Title, List<MemberGroup> Groups);

internal sealed record MemberGroup(string Heading, string Anchor, List<MemberInfo> Members);

/// <summary>Astro's heading ids: github-slugger, one counter per page.</summary>
internal sealed class Slugger {
    private readonly Dictionary<string, int> seen = [];

    public string Slug(string heading) {
        string slug = Regex.Replace(heading.ToLowerInvariant(), @"[^\p{L}\p{M}\p{Nd}\p{Nl}\p{Pc} -]", "").Replace(' ', '-');
        string result = slug;
        if (seen.TryGetValue(slug, out int n)) {
            do {
                n++;
                result = $"{slug}-{n}";
            } while (seen.ContainsKey(result));
            seen[slug] = n;
        }
        else seen[slug] = 0;
        seen.TryAdd(result, 0);
        return result;
    }
}
