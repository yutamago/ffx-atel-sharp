// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ffx.Atel.Cuts;

/// <summary>A named dead block (<c>Cut.*</c>): what the switch turns on.</summary>
/// <param name="Name">The name, a constant of <c>Ffx.Cut</c>.</param>
/// <param name="Summary">Its doc comment's summary, as plain text.</param>
/// <param name="Signatures">The <c>DeadSignature</c>s of its blocks, sorted.</param>
/// <param name="Scripts">The scripts its blocks are in (<c>znkd1300</c>, <c>system_01</c>).</param>
public sealed record CutInfo(string Name, string Summary, string[] Signatures, string[] Scripts);

/// <summary>
///     The names the mod can switch: <c>data/cut-names.toml</c> (signatures and scripts) and the doc comments of
///     <c>src/Ffx.Atel.Game/Cut.cs</c> (what each block does), both embedded in the mod at build time
///     (docs/research/dead-code.md, "Switching a block on in game").
/// </summary>
/// <remarks>
///     The names file is the subset of TOML the decompiler reads (<c>CutNames</c>): <c>#</c> comments, a
///     <c>[cuts]</c> section of <c>"signature" = "Name"</c> lines and a <c>[scripts]</c> section of
///     <c>Name = ["script", …]</c> lines. The mod does not reference the decompiler, so it reads the file itself; a
///     test checks that both readers agree.
/// </remarks>
public sealed class CutCatalog {
    private CutCatalog(IReadOnlyList<CutInfo> cuts) {
        Cuts = cuts;
        BySignature = cuts.SelectMany(c => c.Signatures.Select(s => (s, c))).ToDictionary(x => x.s, x => x.c, StringComparer.Ordinal);
        Scripts = cuts.SelectMany(c => c.Scripts).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every name, in the order of the names file.</summary>
    public IReadOnlyList<CutInfo> Cuts { get; }

    /// <summary>The name each signature belongs to.</summary>
    public IReadOnlyDictionary<string, CutInfo> BySignature { get; }

    /// <summary>The scripts any named block is in: only these are scanned when the game loads them.</summary>
    public IReadOnlySet<string> Scripts { get; }

    /// <summary>The names whose blocks are in <paramref name="script"/>.</summary>
    public IEnumerable<CutInfo> In(string script) => Cuts.Where(c => c.Scripts.Contains(script, StringComparer.Ordinal));

    /// <summary>The resource names of the two embedded files.</summary>
    public const string NamesResource = "cut-names.toml", DocsResource = "Cut.cs";

    /// <summary>The catalog embedded in this assembly.</summary>
    public static CutCatalog Embedded() {
        System.Reflection.Assembly assembly = typeof(CutCatalog).Assembly;
        return Parse(Read(NamesResource), Read(DocsResource));

        string Read(string resource) {
            using Stream stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"the mod is built without {resource}");
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>Reads the names file and the doc comments of <c>Cut.cs</c>.</summary>
    /// <exception cref="FormatException">A line of the names file is not a section, a pair or a comment.</exception>
    public static CutCatalog Parse(string names, string cutSource) {
        Dictionary<string, List<string>> signatures = new(StringComparer.Ordinal);
        Dictionary<string, string[]> scripts = new(StringComparer.Ordinal);
        List<string> order = [];
        string? section = null;
        string[] lines = names.Split('\n');
        for (int n = 0; n < lines.Length; n++) {
            string line = StripComment(lines[n]).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[')) {
                section = line is "[cuts]" or "[scripts]" ? line : throw new FormatException($"cut-names.toml:{n + 1}: unknown section {line}");
                continue;
            }
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || section is null) throw new FormatException($"cut-names.toml:{n + 1}: expected a key = value line in [cuts] or [scripts]");
            string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
            if (section == "[cuts]") {
                string name = Unquote(value, n);
                if (!signatures.TryGetValue(name, out List<string>? list)) {
                    signatures[name] = list = [];
                    order.Add(name);
                }
                list.Add(Unquote(key, n));
            }
            else {
                if (value.Length < 2 || value[0] != '[' || value[^1] != ']') throw new FormatException($"cut-names.toml:{n + 1}: expected a list of script names");
                scripts[key] = [.. value[1..^1].Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Select(x => Unquote(x, n))];
            }
        }
        Dictionary<string, string> docs = Summaries(cutSource);
        return new CutCatalog([.. order.Select(name => new CutInfo(name,
            docs.GetValueOrDefault(name, ""),
            [.. signatures[name].Order(StringComparer.Ordinal)],
            scripts.GetValueOrDefault(name, [])))]);
    }

    private static string StripComment(string line) {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++) {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == '#' && !quoted) return line[..i];
        }
        return line;
    }

    private static string Unquote(string s, int n) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : throw new FormatException($"cut-names.toml:{n + 1}: {s} is not a quoted string");

    private static readonly Regex DocAndConstant = new(
        @"((?:^[ \t]*///.*\r?\n)+)[ \t]*public\s+const\s+bool\s+(\w+)\s*=", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>The summary of each <c>const bool</c> in <paramref name="source"/> (<c>Cut.cs</c>), as plain text.</summary>
    public static Dictionary<string, string> Summaries(string source) {
        Dictionary<string, string> docs = new(StringComparer.Ordinal);
        foreach (Match m in DocAndConstant.Matches(source)) {
            string xml = string.Join("\n", m.Groups[1].Value.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("///", StringComparison.Ordinal)).Select(l => l[3..]));
            try {
                XElement doc = XElement.Parse($"<doc>{xml}</doc>");
                if (doc.Element("summary") is { } summary) docs[m.Groups[2].Value] = PlainText(summary);
            }
            catch (System.Xml.XmlException) {
                // A broken comment shows no summary; the name still switches.
            }
        }
        return docs;
    }

    /// <summary>The text of a doc comment element: <c>&lt;see cref="X"/&gt;</c> as <c>X</c>, whitespace collapsed.</summary>
    private static string PlainText(XElement element) {
        StringBuilder sb = new();
        foreach (XNode node in element.Nodes()) {
            if (node is XText text) sb.Append(text.Value);
            else if (node is XElement e && e.Name == "see" && e.Attribute("cref") is { } cref) sb.Append(cref.Value[(cref.Value.LastIndexOf(':') + 1)..]);
            else if (node is XElement inner) sb.Append(PlainText(inner));
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
