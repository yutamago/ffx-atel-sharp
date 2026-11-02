// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Text;

namespace Ffx.Atel.Linker;

/// <summary>
///     New messages (<c>Text.New</c>) in a script's message tables (docs/formats/mod-package.md, "Messages"): a
///     script holds one index for all languages, so every language's table gets the message at the same index,
///     after the last message of the longest of them. The mod's source language gets the text, the others a
///     copy of it, which the report lists as untranslated.
/// </summary>
public static class MessageLinker {
    /// <summary>
    ///     The archive paths of a script's message tables, one per language; empty for a monster (its messages
    ///     are the battle's).
    /// </summary>
    public static IEnumerable<(GameLanguage Language, string Path)> TablePaths(ScriptCategory category, string name) =>
        category is ScriptCategory.Event or ScriptCategory.Battle
            ? GameLanguages.All.Select(l => (l, ScriptMessages.TablePath(category, name, l)!))
            : [];

    /// <summary>
    ///     The first index no table of the script uses: one past the last message of the longest of its
    ///     language tables and, for an event, of the <c>.ebp</c>'s own (Japanese) table, which the game reads
    ///     where a language has none. Null when the script has no table (a monster, a battle without texts).
    /// </summary>
    /// <param name="scriptPath">The script's archive path.</param>
    /// <param name="read">Reads an archive path; null when the game has no such file.</param>
    public static int? FirstFree(string scriptPath, Func<string, byte[]?> read) {
        if (GameData.Identify(scriptPath) is not var (category, name)) return null;
        int? count = null;
        foreach ((_, string path) in TablePaths(category, name)) {
            if (Parse(read(path)) is MessageTable t) count = Math.Max(count ?? 0, t.Messages.Count);
        }
        if (category == ScriptCategory.Event && read(scriptPath) is byte[] ebp && ScriptMessages.OriginalTable(ebp) is MessageTable original) {
            count = Math.Max(count ?? 0, original.Messages.Count);
        }
        return count;
    }

    private static MessageTable? Parse(byte[]? bytes) {
        if (bytes is null) return null;
        try {
            return MessageTable.Parse(bytes);
        }
        catch (InvalidDataException) {
            return null;
        }
    }

    /// <summary>
    ///     The script's tables with the new messages: for every language that has a table, the vanilla table,
    ///     grown to the highest new index (entries between get an empty text), with each new message encoded in
    ///     the language's charset. A message written in another language than the table's is a copy (reported
    ///     as untranslated); a character the language's charset lacks becomes its full-width form (the
    ///     Japanese, Korean and Chinese charsets have no ASCII letters) or <c>?</c> (a warning).
    /// </summary>
    /// <returns>The archive path and bytes of each table that changed.</returns>
    public static List<(string Path, byte[] Bytes)> Tables(string scriptPath, IReadOnlyList<LinkedMessage> added, Func<string, byte[]?> read, List<LinkMessage> report) {
        List<(string, byte[])> tables = [];
        if (added.Count == 0 || GameData.Identify(scriptPath) is not var (category, name)) return tables;
        Dictionary<string, Charset> charsets = [];
        List<string> missing = [];
        foreach ((GameLanguage language, string path) in TablePaths(category, name)) {
            if (Parse(read(path)) is not MessageTable vanilla) {
                missing.Add(language.IsoCode());
                continue;
            }
            string charsetPath = language.CharsetPath();
            if (!charsets.TryGetValue(charsetPath, out Charset? charset)) {
                if (read(charsetPath) is not byte[] table) {
                    report.Add(new(LinkSeverity.Error, "", $"{name}: no charset {charsetPath}; the {language.IsoCode()} table is left as it is"));
                    continue;
                }
                charsets[charsetPath] = charset = new Charset(table, language);
            }
            List<Message> messages = [.. vanilla.Messages];
            foreach (LinkedMessage m in added) {
                while (messages.Count <= m.Index) messages.Add(new Message([], 0));
                messages[m.Index] = new Message(Encode(charset, m, language, name, report), 0);
            }
            foreach (IGrouping<string, LinkedMessage> byMod in added.Where(m => GameLanguages.Parse(m.SourceLanguage) != language).GroupBy(m => m.Mod)) {
                report.Add(new(LinkSeverity.Info, byMod.Key, $"{name}: untranslated in {language.IsoCode()} (the {byMod.First().SourceLanguage} text): message(s) {string.Join(", ", byMod.Select(m => m.Index))}"));
            }
            try {
                tables.Add((path, new MessageTable(messages).Write()));
            }
            catch (InvalidDataException ex) {
                report.Add(new(LinkSeverity.Error, "", $"{name}: the {language.IsoCode()} table: {ex.Message}; left as it is"));
            }
        }
        if (missing.Count > 0) {
            report.Add(new(LinkSeverity.Warning, string.Join(", ", added.Select(m => m.Mod).Distinct()),
                $"{name} has no message table in {string.Join(", ", missing)}; the new messages have no text there"));
        }
        return tables;
    }

    /// <summary>The message in a language's charset; a character it lacks in its full-width form, else as <c>?</c>.</summary>
    private static byte[] Encode(Charset charset, LinkedMessage m, GameLanguage language, string script, List<LinkMessage> report) {
        try {
            return charset.Encode(m.Text);
        }
        catch (TextEncodeException ex) {
            report.Add(new(LinkSeverity.Warning, m.Mod, $"{script}: message {m.Index} in {language.IsoCode()}: {ex.Message}; written with full-width characters, or ?, for what the charset lacks"));
        }
        // Character by character outside the braces (a control code is kept or dropped whole).
        List<byte> bytes = [];
        byte[] fallback = TryEncode(charset, "?") ?? TryEncode(charset, "\uff1f") ?? [];
        for (int i = 0; i < m.Text.Length;) {
            int len = 1;
            if (m.Text[i] is '{' or '}' && i + 1 < m.Text.Length && m.Text[i + 1] == m.Text[i]) {
                len = 2;
            }
            else if (m.Text[i] == '{') {
                int close = m.Text.IndexOf('}', i);
                len = close < 0 ? m.Text.Length - i : close - i + 1;
            }
            else if (char.IsHighSurrogate(m.Text[i]) && i + 1 < m.Text.Length) {
                len = 2;
            }
            string piece = m.Text.Substring(i, len);
            bytes.AddRange(TryEncode(charset, piece)
                ?? (FullWidth(piece) is string wide ? TryEncode(charset, wide) : null)
                ?? (piece == " " && language == GameLanguage.Korean ? TryEncode(charset, KoreanSpace) : null)
                ?? fallback);
            i += len;
        }
        return [.. bytes];
    }

    /// <summary>
    ///     The full-width form of an ASCII character (<c>A</c> to <c>Ａ</c>, space to U+3000): the Japanese, Korean
    ///     and Chinese charsets have those, not ASCII letters.
    /// </summary>
    private static string? FullWidth(string piece) => piece switch {
        " " => "\u3000",
        [char c] when c is > ' ' and <= '~' => ((char)(c - 0x21 + 0xff01)).ToString(),
        _ => null,
    };

    /// <summary>The Korean space: character 10, which the game shows as a space in Korean (<c>0x006460a0</c>).</summary>
    private const string KoreanSpace = "{c:a}";

    private static byte[]? TryEncode(Charset charset, string text) {
        try {
            return charset.Encode(text);
        }
        catch (TextEncodeException) {
            return null;
        }
    }
}
