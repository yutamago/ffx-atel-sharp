// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler.Layout;

/// <summary>
///     Hand-written names for the inline methods the decompiler writes for a worker's repeated code
///     (<c>data/inline-names.toml</c>, embedded in the decompiler): by the signature of the method's body
///     (<see cref="Signature"/>), a name and, optionally, the parameters' names and a summary.
/// </summary>
public sealed partial class InlineNames {
    /// <summary>A method's name, its parameters' names in order (null: the decompiler's), and what it does (null: none).</summary>
    public sealed record Entry(string Name, IReadOnlyList<string>? Params, string? Summary);

    private readonly Dictionary<string, Entry> _names;

    private InlineNames(Dictionary<string, Entry> names, Dictionary<string, string> summaries) =>
        _names = names.ToDictionary(x => x.Key, x => x.Value.Summary is null && summaries.TryGetValue(x.Value.Name, out string? s) ? x.Value with { Summary = s } : x.Value, StringComparer.Ordinal);

    /// <summary>No names.</summary>
    public static InlineNames Empty { get; } = new([], []);

    /// <summary>This repo's <c>data/inline-names.toml</c>.</summary>
    public static InlineNames Default => s_default.Value;

    private static readonly Lazy<InlineNames> s_default = new(() => {
        using Stream? stream = typeof(InlineNames).Assembly.GetManifestResourceStream("inline-names.toml");
        if (stream is null) return Empty;
        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd(), "data/inline-names.toml");
    });

    /// <summary>The name, parameter names and summary for a body's signature, if the table has them.</summary>
    public Entry? Find(string signature) => _names.GetValueOrDefault(signature);

    /// <summary>How many signatures the table names.</summary>
    public int Count => _names.Count;

    /// <summary>The signatures the table names.</summary>
    public IEnumerable<string> Signatures => _names.Keys;

    /// <summary>The number of hex digits in a signature (64 bits).</summary>
    public const int Length = 16;

    /// <summary>
    ///     A body's signature: 16 hex digits of a hash of its statements as lifted from the bytecode, not of the C#
    ///     the decompiler writes for them, so a name stays when the C# changes (new member names, typed
    ///     constants), as a library class's <see cref="WorkerSignature"/> does.
    /// </summary>
    /// <remarks>
    ///     Natives, operators and opcodes go by their numbers, constants by value, a variable by its location word,
    ///     length and row length (<paramref name="table"/>: the script's variable table), registers by kind and
    ///     number, parameters by their index, and a call of a reference-assembly inline method (<c>Dialogue.Say</c>)
    ///     by the statements it stands for. A dead block's <c>Cut</c> name is left out. So identical code hashes the
    ///     same in every worker and script whatever its variables are called.
    /// </remarks>
    /// <exception cref="ArgumentException">A statement or expression the signature does not know.</exception>
    public static string Signature(IReadOnlyList<Stmt> body, IReadOnlyList<VarEntry> table) {
        StringBuilder sb = new("inline\n");
        Statements(sb, body, table);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash)[..Length];
    }

    private static void Statements(StringBuilder sb, IReadOnlyList<Stmt> list, IReadOnlyList<VarEntry> t) {
        sb.Append('{');
        foreach (Stmt s in list) {
            Statement(sb, s, t);
            sb.Append(';');
        }
        sb.Append('}');
    }

    private static void Statement(StringBuilder sb, Stmt s, IReadOnlyList<VarEntry> t) {
        switch (s) {
            case AssignStmt a:
                sb.Append(a.Clamped ? "set! " : "set ");
                Expression(sb, a.Target, t);
                sb.Append(' ');
                Expression(sb, a.Value, t);
                break;
            case CompoundStmt c:
                sb.Append(CultureInfo.InvariantCulture, $"op{c.Op:x2}= ");
                Expression(sb, c.Target, t);
                sb.Append(' ');
                Expression(sb, c.Value, t);
                break;
            case CallStmt c:
                sb.Append(CultureInfo.InvariantCulture, $"call {c.Native:x4}");
                Arguments(sb, c.Args, t);
                break;
            case RequestStmt r:
                sb.Append(CultureInfo.InvariantCulture, $"req{r.Op:x2}");
                Arguments(sb, [r.Level, r.Worker, r.Entry], t);
                break;
            case CallWorkerStmt w: sb.Append(CultureInfo.InvariantCulture, $"callw {w.Worker}"); break;
            case OpStmt o: sb.Append(CultureInfo.InvariantCulture, $"op {o.Op:x2}"); break;
            case OpArgsStmt o:
                sb.Append(CultureInfo.InvariantCulture, $"op {o.Op:x2}");
                Arguments(sb, o.Args, t);
                break;
            case NopStmt n: sb.Append(CultureInfo.InvariantCulture, $"nop {n.Operand}"); break;
            case EndWithStmt e:
                sb.Append(CultureInfo.InvariantCulture, $"end {e.Op:x2} ");
                Expression(sb, e.Value, t);
                break;
            case IfStmt i:
                sb.Append("if ");
                Condition(sb, i.Condition, t);
                Statements(sb, i.Then, t);
                if (i.Else is not null) {
                    sb.Append("else");
                    Statements(sb, i.Else, t);
                }
                break;
            case WhileStmt w:
                sb.Append("while ");
                Condition(sb, w.Condition, t);
                Statements(sb, w.Body, t);
                break;
            case DoStmt d:
                sb.Append("do");
                Statements(sb, d.Body, t);
                Expression(sb, d.Condition, t);
                break;
            case ForStmt f:
                sb.Append("for");
                Statements(sb, f.Init, t);
                Condition(sb, f.Condition, t);
                Statements(sb, f.Step, t);
                Statements(sb, f.Body, t);
                break;
            case SwitchStmt sw:
                sb.Append("switch ");
                Expression(sb, sw.Selector, t);
                foreach (SwitchSection x in sw.Sections) {
                    sb.Append(x.Label is CaseLabel l ? string.Create(CultureInfo.InvariantCulture, $" case {l.Kind} {l.Value} {l.High}") : " default");
                    Statements(sb, x.Body, t);
                }
                break;
            case BreakStmt: sb.Append("break"); break;
            case ContinueStmt: sb.Append("continue"); break;
            case GotoCaseStmt g: sb.Append(CultureInfo.InvariantCulture, $"goto case {g.Case?.ToString(CultureInfo.InvariantCulture) ?? "default"}"); break;
            case InlineStmt x: Statements(sb, x.Body, t); break;
            default: throw new ArgumentException($"no signature for {s.GetType().Name}", nameof(s));
        }
    }

    private static void Condition(StringBuilder sb, Expr? e, IReadOnlyList<VarEntry> t) {
        if (e is null) sb.Append("const");
        else Expression(sb, e, t);
    }

    private static void Arguments(StringBuilder sb, IReadOnlyList<Expr> args, IReadOnlyList<VarEntry> t) {
        sb.Append('(');
        foreach (Expr a in args) {
            Expression(sb, a, t);
            sb.Append(',');
        }
        sb.Append(')');
    }

    private static void Expression(StringBuilder sb, Expr e, IReadOnlyList<VarEntry> t) {
        switch (e) {
            case IntLit i: sb.Append(CultureInfo.InvariantCulture, $"={i.Value}"); break;
            case FloatLit f: sb.Append(CultureInfo.InvariantCulture, $"=f{f.Bits:x8}"); break;
            case PoolExpr p: sb.Append(CultureInfo.InvariantCulture, $"=pool{p.Code:x2}:{p.Value:x8}"); break;
            case ParamExpr p: sb.Append(CultureInfo.InvariantCulture, $"p{p.Index}"); break;
            case VarExpr v: Variable(sb, v.Var, t); break;
            case ElemExpr x:
                Variable(sb, x.Var, t);
                sb.Append('[');
                Expression(sb, x.Index, t);
                sb.Append(']');
                break;
            case Elem2Expr x:
                Variable(sb, x.Var, t);
                sb.Append('[');
                Expression(sb, x.Row, t);
                sb.Append(',');
                Expression(sb, x.Col, t);
                sb.Append(CultureInfo.InvariantCulture, $"/{x.RowLength}]");
                break;
            case AddrExpr a:
                sb.Append('&');
                Expression(sb, a.Element, t);
                break;
            case RegExpr r: sb.Append(CultureInfo.InvariantCulture, $"r{r.Kind}{r.Number}"); break;
            case UnaryExpr u:
                sb.Append(CultureInfo.InvariantCulture, $"(u{u.Op:x2} ");
                Expression(sb, u.Operand, t);
                sb.Append(')');
                break;
            case BinaryExpr b:
                sb.Append(CultureInfo.InvariantCulture, $"(b{b.Op:x2} ");
                Expression(sb, b.Left, t);
                sb.Append(' ');
                Expression(sb, b.Right, t);
                sb.Append(')');
                break;
            case AndAlsoExpr a:
                sb.Append("(&& ");
                Expression(sb, a.Left, t);
                sb.Append(' ');
                Expression(sb, a.Right, t);
                sb.Append(')');
                break;
            case OrElseExpr o:
                sb.Append("(|| ");
                Expression(sb, o.Left, t);
                sb.Append(' ');
                Expression(sb, o.Right, t);
                sb.Append(')');
                break;
            case CallExpr c:
                sb.Append(CultureInfo.InvariantCulture, $"call {c.Native:x4}");
                Arguments(sb, c.Args, t);
                break;
            default: throw new ArgumentException($"no signature for {e.GetType().Name}", nameof(e));
        }
    }

    private static void Variable(StringBuilder sb, int v, IReadOnlyList<VarEntry> t) {
        if (v < 0 || v >= t.Count) throw new ArgumentException($"variable {v} is not in the table", nameof(v));
        VarEntry x = t[v];
        sb.Append(CultureInfo.InvariantCulture, $"v{x.Location:x8}:{x.Length}:{x.RowLength}");
    }

    /// <summary>
    ///     Reads a names file: under <c>[helpers]</c>, <c>"signature" = "Name"</c>, <c>"Name(a, b)"</c>, or
    ///     <c>{ name = "Name(a, b)", summary = "What it does." }</c>; under <c>[summaries]</c>, <c>Name = "What it does."</c>
    ///     for the methods of that name that give none. Strings have no escapes.
    /// </summary>
    /// <exception cref="FormatException">A line is not a section, a pair or a comment; a name not a C# name; a key given twice.</exception>
    public static InlineNames Parse(string text, string source) {
        Dictionary<string, Entry> names = new(StringComparer.Ordinal);
        Dictionary<string, string> summaries = new(StringComparer.Ordinal);
        string? section = null;
        string[] lines = text.Split('\n');
        for (int n = 0; n < lines.Length; n++) {
            string line = Naming.NameOverlay.StripComment(lines[n]).Trim();
            if (line.Length == 0) continue;
            string where = string.Create(CultureInfo.InvariantCulture, $"{source}:{n + 1}");
            if (line is "[helpers]" or "[summaries]") {
                section = line;
                continue;
            }
            if (section is null) throw new FormatException($"{where}: start with [helpers]");
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) throw new FormatException($"{where}: expected \"signature\" = \"Name\"");
            string key = Naming.NameOverlay.Unquote(line[..eq].Trim());
            string value = line[(eq + 1)..].Trim();
            if (section == "[summaries]") {
                if (!Naming.NameOverlay.IsIdentifier(key)) throw new FormatException($"{where}: \"{key}\" is not a C# name");
                if (value.Length < 2 || value[0] != '"' || value[^1] != '"' || value[1..^1].Contains('"', StringComparison.Ordinal)) {
                    throw new FormatException($"{where}: the summary is a quoted string");
                }
                if (!summaries.TryAdd(key, value[1..^1])) throw new FormatException($"{where}: {key} has two summaries");
                continue;
            }
            if (key.Length != Length || !key.All(char.IsAsciiHexDigitLower)) throw new FormatException($"{where}: a key is a signature, 16 lower-case hex digits");
            string? summary = null;
            if (value.StartsWith('{')) {
                Match m = Table().Match(value);
                if (!m.Success) throw new FormatException($"{where}: expected {{ name = \"Name\", summary = \"…\" }}");
                value = m.Groups["name"].Value;
                summary = m.Groups["summary"].Success ? m.Groups["summary"].Value : null;
            }
            else {
                if (value.Length < 2 || value[0] != '"' || value[^1] != '"' || value[1..^1].Contains('"', StringComparison.Ordinal)) {
                    throw new FormatException($"{where}: the value is a quoted string or {{ name = …, summary = … }}");
                }
                value = value[1..^1];
            }
            string name = value;
            IReadOnlyList<string>? ps = null;
            int open = value.IndexOf('(', StringComparison.Ordinal);
            if (open >= 0) {
                if (!value.EndsWith(')')) throw new FormatException($"{where}: Name(a, b)");
                name = value[..open];
                ps = [.. value[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
                if (ps.Any(p => !Naming.NameOverlay.IsIdentifier(p))) throw new FormatException($"{where}: a parameter is a C# name");
                if (ps.Distinct(StringComparer.Ordinal).Count() != ps.Count) throw new FormatException($"{where}: a parameter is named twice");
            }
            if (!Naming.NameOverlay.IsIdentifier(name)) throw new FormatException($"{where}: \"{name}\" is not a C# name");
            if (!names.TryAdd(key, new Entry(name, ps, summary))) throw new FormatException($"{where}: {key} is named twice");
        }
        return new InlineNames(names, summaries);
    }

    [GeneratedRegex("""^\{\s*name\s*=\s*"(?<name>[^"]*)"\s*(,\s*summary\s*=\s*"(?<summary>[^"]*)"\s*)?\}$""")]
    private static partial Regex Table();
}
