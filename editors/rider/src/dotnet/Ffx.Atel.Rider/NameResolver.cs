// SPDX-License-Identifier: MIT
//
// The game's names behind typed values (`atel names export`, docs/editor.md "Names"), without the IDE: which table a
// type's values index, the files and their lookup. The same rules as the VS Code extension's resolve.ts.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Ffx.Atel.Rider
{
    /// <summary>One name: the value the source uses, its member if the type has one, the game's name.</summary>
    public sealed class NameEntry
    {
        public NameEntry(int id, string member, string name)
        {
            Id = id;
            Member = member;
            Name = name;
        }

        public int Id { get; }
        public string Member { get; }
        public string Name { get; }
    }

    /// <summary>A file <c>atel names export</c> writes: <c>&lt;root&gt;/&lt;lang&gt;/&lt;table&gt;.json</c>, indexed.</summary>
    public sealed class NamesFile
    {
        private readonly Dictionary<int, NameEntry> myById = new Dictionary<int, NameEntry>();
        private readonly Dictionary<string, NameEntry> myByMember = new Dictionary<string, NameEntry>(StringComparer.Ordinal);

        public NamesFile(string table, string type, string language, IEnumerable<NameEntry> names)
        {
            Table = table;
            Type = type;
            Language = language;
            foreach (var e in names)
            {
                myById[e.Id] = e;
                if (e.Member != null) myByMember[e.Member] = e;
            }
        }

        public string Table { get; }
        public string Type { get; }
        public string Language { get; }

        /// <summary>The entry of a member (<c>Biran</c>) or, without one, of an id; null when the table has none.</summary>
        public NameEntry Find(string member, int? id)
        {
            NameEntry entry;
            if (member != null) return myByMember.TryGetValue(member, out entry) ? entry : null;
            return id.HasValue && myById.TryGetValue(id.Value, out entry) ? entry : null;
        }
    }

    public static class NameResolver
    {
        /// <summary>The table each type's values index.</summary>
        public static readonly IReadOnlyDictionary<string, string> Tables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Target", "monsters" },
            { "Command", "commands" },
            { "Map", "maps" },
            { "Battle", "battles" },
        };

        /// <summary>The nearest <c>.atel/names</c> folder at or above a file's folder; null when there is none.</summary>
        public static string FindNamesRoot(string filePath, Func<string, bool> exists = null)
        {
            exists = exists ?? Directory.Exists;
            var dir = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, ".atel", "names");
                if (exists(candidate)) return candidate;
                var parent = Path.GetDirectoryName(dir);
                if (parent == null || parent == dir) return null;
                dir = parent;
            }

            return null;
        }

        /// <summary>The file of a type's table; null for a type without one.</summary>
        public static string NamesFilePath(string root, string language, string type) =>
            Tables.TryGetValue(type, out var table) ? Path.Combine(root, language, table + ".json") : null;

        /// <summary>A names file's contents; null when it isn't one.</summary>
        public static NamesFile ParseNamesFile(string json)
        {
            try
            {
                if (!(JToken.Parse(json) is JObject data) || !(data["names"] is JArray names)) return null;
                var entries = new List<NameEntry>();
                foreach (var token in names)
                {
                    if (!(token is JObject e) || e["id"]?.Type != JTokenType.Integer || e["name"]?.Type != JTokenType.String) continue;
                    var member = e["member"]?.Type == JTokenType.String ? (string)e["member"] : null;
                    entries.Add(new NameEntry((int)e["id"], member, (string)e["name"]));
                }

                return new NamesFile((string)data["table"] ?? "", (string)data["type"] ?? "", (string)data["language"] ?? "", entries);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>An integer literal's value (<c>4097</c>, <c>0x1001</c>); null for anything else.</summary>
        public static int? ParseInteger(string literal)
        {
            if (string.IsNullOrEmpty(literal)) return null;
            if (literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(literal.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) ? hex : (int?)null;
            return int.TryParse(literal, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : (int?)null;
        }

        /// <summary>How the hint's tooltip names the value: <c>Target.Biran (0x1086, en)</c>.</summary>
        public static string Describe(string type, NameEntry entry, string language)
        {
            var id = "0x" + entry.Id.ToString("x", CultureInfo.InvariantCulture);
            var what = entry.Member != null ? type + "." + entry.Member : "(" + type + ")" + id;
            return $"{what} ({id}, {language}): {entry.Name}";
        }
    }
}
