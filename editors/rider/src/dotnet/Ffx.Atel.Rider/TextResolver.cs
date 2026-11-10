// SPDX-License-Identifier: MIT
//
// What a source file's Text[n] refers to, without the IDE: which script's message table (from the file's text and
// path) and where the exported tables are. The same rules as the VS Code extension's resolve.ts (docs/editor.md).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Ffx.Atel.Rider
{
    /// <summary>What kind of script a file belongs to; monsters have no message table of their own.</summary>
    public enum ScriptCategory
    {
        Unknown,
        Event,
        Battle,
        Monster,
    }

    public sealed class ScriptRef : IEquatable<ScriptRef>
    {
        public ScriptRef(string name, ScriptCategory category)
        {
            Name = name;
            Category = category;
        }

        public string Name { get; }
        public ScriptCategory Category { get; }

        public bool Equals(ScriptRef other) => other != null && Name == other.Name && Category == other.Category;
        public override bool Equals(object obj) => Equals(obj as ScriptRef);
        public override int GetHashCode() => Name.GetHashCode() * 31 + (int)Category;
        public override string ToString() => $"{Name} ({Category})";
    }

    /// <summary>A file <c>atel text export</c> writes: <c>&lt;root&gt;/&lt;lang&gt;/&lt;script&gt;.json</c>.</summary>
    public sealed class TextFile
    {
        public TextFile(string script, string language, IReadOnlyList<string> messages)
        {
            Script = script;
            Language = language;
            Messages = messages;
        }

        public string Script { get; }
        public string Language { get; }

        /// <summary>A message's index is its position; null is a message without text.</summary>
        public IReadOnlyList<string> Messages { get; }

        /// <summary>Whether the table has an entry <paramref name="index" /> (its text may still be null).</summary>
        public bool Has(int index) => index >= 0 && index < Messages.Count;
    }

    public static class TextResolver
    {
        /// <summary>The languages <c>atel text export</c> writes, with their names.</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, string>> Languages = new[]
        {
            new KeyValuePair<string, string>("en", "English"),
            new KeyValuePair<string, string>("ja", "Japanese"),
            new KeyValuePair<string, string>("fr", "French"),
            new KeyValuePair<string, string>("es", "Spanish"),
            new KeyValuePair<string, string>("de", "German"),
            new KeyValuePair<string, string>("it", "Italian"),
            new KeyValuePair<string, string>("ko", "Korean"),
            new KeyValuePair<string, string>("zh", "Chinese"),
        };

        private static readonly Regex Decompiled =
            new Regex(@"^// ((?:event|battle)/[^\s,]+), decompiled by atel\.", RegexOptions.CultureInvariant);

        private static readonly Regex Attribute = new Regex(
            @"\[\s*(?:Ffx\.Atel\.)?(Event|Battle|Monster)(?:Attribute)?\s*\(\s*""([^""]+)""\s*\)",
            RegexOptions.CultureInvariant);

        private static readonly Regex LibraryHead =
            new Regex(@"^// The code of \d+ workers, decompiled by atel:", RegexOptions.CultureInvariant);

        private static readonly Regex LibraryUser =
            new Regex(@"^// {3}([A-Za-z_][\w.]*)\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex Namespace =
            new Regex(@"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>How much of a file's start names its script (both editors read only this much): a decompiled worker
        /// can be megabytes (bltz0000's W36), and what names the script is always at the top.</summary>
        public const int HeadLength = 65536;

        private static ScriptCategory CategoryOf(string rootNamespace)
        {
            switch (rootNamespace)
            {
                case "Events": return ScriptCategory.Event;
                case "Battles": return ScriptCategory.Battle;
                case "Monsters": return ScriptCategory.Monster;
                default: return ScriptCategory.Unknown;
            }
        }

        /// <summary>A script's name from its namespace (<c>Events.Bj.Bjyt0400</c> → <c>bjyt0400</c>).</summary>
        private static ScriptRef FromNamespace(string ns)
        {
            var parts = ns.Split('.');
            var category = CategoryOf(parts[0]);
            if (category == ScriptCategory.Unknown || parts.Length < 2) return null;
            return new ScriptRef(parts[parts.Length - 1].ToLowerInvariant(), category);
        }

        /// <summary>
        /// The scripts whose message table a file's <c>Text[n]</c> reads, most certain first (all from the file's first
        /// <see cref="HeadLength" /> characters):
        /// 1. the first line <c>atel decompile</c> writes: <c>// event/obj/bj/bjyt0400/bjyt0400.ebp, decompiled by atel.</c>;
        /// 2. the script attribute: <c>[Event("bjyt0400")]</c>, <c>[Battle("…")]</c>, <c>[Monster("…")]</c>;
        /// 3. a library class (<c>Library/Shared0001.cs</c>), shared by workers of several scripts: each listed user
        ///    (<c>//   Events.Bj.Bjyt0400.W03</c>);
        /// 4. the namespace: <c>namespace Events.Bj.Bjyt0400;</c>;
        /// 5. the folder's name.
        /// </summary>
        public static IReadOnlyList<ScriptRef> ScriptsOf(string text, string filePath)
        {
            var head = text.Length > HeadLength ? text.Substring(0, HeadLength) : text;
            var decompiled = Decompiled.Match(head);
            if (decompiled.Success)
            {
                var p = decompiled.Groups[1].Value;
                var file = p.Substring(p.LastIndexOf('/') + 1);
                var dot = file.LastIndexOf('.');
                var name = (dot > 0 ? file.Substring(0, dot) : file).ToLowerInvariant();
                var category = p.StartsWith("event/", StringComparison.Ordinal) ? ScriptCategory.Event
                    : p.StartsWith("battle/mon/", StringComparison.Ordinal) ? ScriptCategory.Monster
                    : ScriptCategory.Battle;
                return new[] { new ScriptRef(name, category) };
            }

            var attribute = Attribute.Match(head);
            if (attribute.Success)
            {
                var category = (ScriptCategory)Enum.Parse(typeof(ScriptCategory), attribute.Groups[1].Value);
                return new[] { new ScriptRef(attribute.Groups[2].Value.ToLowerInvariant(), category) };
            }

            if (LibraryHead.IsMatch(head))
            {
                var users = new List<ScriptRef>();
                foreach (Match m in LibraryUser.Matches(head))
                {
                    var parts = m.Groups[1].Value.Split('.');
                    var reference = FromNamespace(string.Join(".", parts.Take(parts.Length - 1)));
                    if (reference != null && users.All(u => u.Name != reference.Name)) users.Add(reference);
                }

                return users;
            }

            var ns = Namespace.Match(head);
            var fromNamespace = ns.Success ? FromNamespace(ns.Groups[1].Value) : null;
            if (fromNamespace != null) return new[] { fromNamespace };

            var folder = Path.GetFileName(Path.GetDirectoryName(filePath) ?? "");
            return string.IsNullOrEmpty(folder)
                ? Array.Empty<ScriptRef>()
                : new[] { new ScriptRef(folder.ToLowerInvariant(), ScriptCategory.Unknown) };
        }

        /// <summary>The nearest <c>.atel/text</c> folder at or above a file's folder; null when there is none.</summary>
        public static string FindTextRoot(string filePath, Func<string, bool> exists = null)
        {
            exists = exists ?? Directory.Exists;
            var dir = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, ".atel", "text");
                if (exists(candidate)) return candidate;
                var parent = Path.GetDirectoryName(dir);
                if (parent == null || parent == dir) return null;
                dir = parent;
            }

            return null;
        }

        public static string TextFilePath(string root, string language, string script) =>
            Path.Combine(root, language, script + ".json");

        /// <summary>A text file's contents; null when it isn't one.</summary>
        public static TextFile ParseTextFile(string json)
        {
            try
            {
                if (!(JToken.Parse(json) is JObject data) || !(data["messages"] is JArray messages)) return null;
                var list = messages.Select(m => m.Type == JTokenType.String ? (string)m : null).ToList();
                return new TextFile((string)data["script"] ?? "", (string)data["language"] ?? "", list);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static readonly Regex Lines = new Regex("\r?\n", RegexOptions.CultureInvariant);
        private static readonly Regex LeadingWaits = new Regex(@"^(?:\s|\{wait:\d+\})+", RegexOptions.CultureInvariant);
        private static readonly Regex NameCode = new Regex(@"^\{name:([^{}]+)\}\s*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// A message split for showing: who speaks it, when its first line says so, and its lines. A window's first line
        /// is the speaker when it is a character's name code alone (<c>{name:Tidus}</c>, after any <c>{wait:n}</c>); a
        /// first line left empty is a speaker the window does not name. Other first lines (plain names, choices) stay
        /// part of the text. As the VS Code extension's <c>messageParts</c>.
        /// </summary>
        public static (string Speaker, IReadOnlyList<string> Lines) MessageParts(string text)
        {
            var lines = Lines.Split(text);
            if (lines.Length < 2) return (null, lines);
            var first = LeadingWaits.Replace(lines[0], "");
            var name = NameCode.Match(first);
            if (name.Success) return (name.Groups[1].Value, lines.Skip(1).ToList());
            if (first.Trim().Length == 0 && lines[0].IndexOf('{') < 0) return (null, lines.Skip(1).ToList());
            return (null, lines);
        }

        /// <summary>Text cut to <paramref name="max" /> characters with an ellipsis; a word the cut would split goes, when a
        /// space is in the second half.</summary>
        public static string Shorten(string text, int max)
        {
            var info = new System.Globalization.StringInfo(text);
            if (info.LengthInTextElements <= max) return text;
            var keep = Math.Max(1, max - 1);
            var cut = info.SubstringByTextElements(0, keep);
            // Inside a word: back to the space before it.
            var space = cut.LastIndexOf(' ');
            if (info.SubstringByTextElements(keep, 1) != " " && space >= max / 2) cut = cut.Substring(0, space);
            return cut.TrimEnd() + "…";
        }

        /// <summary>A message on one line for an inlay hint: its lines joined by spaces (the window's breaks are only
        /// layout), cut to <paramref name="max" /> characters.</summary>
        public static string HintLabel(string text, int max) =>
            Shorten(string.Join(" ", Lines.Split(text).Select(l => l.Trim()).Where(l => l.Length > 0)), max);

        /// <summary>The inlay hint of a message: <c>Tidus: "Hey, wait! …"</c>, or only the quoted text without a speaker.</summary>
        public static string MessageHint(string text, int max)
        {
            var (speaker, lines) = MessageParts(text);
            var quoted = Quote(HintLabel(string.Join("\n", lines), max));
            return speaker != null ? speaker + ": " + quoted : quoted;
        }

        /// <summary>The whole message for a tooltip: the speaker on a line of its own, then the message's lines.</summary>
        public static string MessageFull(string text)
        {
            var (speaker, lines) = MessageParts(text);
            return (speaker != null ? speaker + ":\n" : "") + string.Join("\n", lines);
        }

        /// <summary>The languages a text (or names) folder has an export for: its subfolders, sorted.</summary>
        public static IReadOnlyList<string> ExportedLanguages(string root)
        {
            try
            {
                return Directory.Exists(root)
                    ? Directory.GetDirectories(root).Select(Path.GetFileName).OrderBy(l => l, StringComparer.Ordinal).ToList()
                    : (IReadOnlyList<string>)Array.Empty<string>();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>A string as a C#/JSON literal, as the VS Code hint shows it: <c>"…"</c>.</summary>
        public static string Quote(string text)
        {
            var sb = new StringBuilder(text.Length + 2).Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }

            return sb.Append('"').ToString();
        }
    }
}
