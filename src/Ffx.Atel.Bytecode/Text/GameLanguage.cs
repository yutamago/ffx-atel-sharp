// SPDX-License-Identifier: MIT

namespace Ffx.Atel.Bytecode.Text;

/// <summary>A language the game ships text for.</summary>
/// <remarks>Ported from ffx-rust's <c>crates/ffx-text</c> (<c>Language</c>); <c>docs/formats/text.md</c> there.</remarks>
public enum GameLanguage {
    Japanese,
    English,
    French,
    Spanish,
    German,
    Italian,
    Korean,
    Chinese,
}

/// <summary>Codes and archive paths of a <see cref="GameLanguage"/>.</summary>
public static class GameLanguages {
    public static IReadOnlyList<GameLanguage> All { get; } = Enum.GetValues<GameLanguage>();

    /// <summary>The two letters the game's folders use: <c>new_uspc</c> for English.</summary>
    public static string GameCode(this GameLanguage language) => language switch {
        GameLanguage.Japanese => "jp",
        GameLanguage.English  => "us",
        GameLanguage.French   => "fr",
        GameLanguage.Spanish  => "sp",
        GameLanguage.German   => "de",
        GameLanguage.Italian  => "it",
        GameLanguage.Korean   => "kr",
        GameLanguage.Chinese  => "ch",
        _                     => throw new ArgumentOutOfRangeException(nameof(language)),
    };

    /// <summary>The ISO 639-1 code: the folder <c>atel text export</c> writes and the editor setting names.</summary>
    public static string IsoCode(this GameLanguage language) => language switch {
        GameLanguage.Japanese => "ja",
        GameLanguage.English  => "en",
        GameLanguage.French   => "fr",
        GameLanguage.Spanish  => "es",
        GameLanguage.German   => "de",
        GameLanguage.Italian  => "it",
        GameLanguage.Korean   => "ko",
        GameLanguage.Chinese  => "zh",
        _                     => throw new ArgumentOutOfRangeException(nameof(language)),
    };

    /// <summary>Parses an ISO code (<c>en</c>), the game's code (<c>us</c>) or the English name, ignoring case.</summary>
    public static GameLanguage? Parse(string code) {
        string c = code.Trim().ToLowerInvariant();
        foreach (GameLanguage l in All) {
            if (c == l.IsoCode() || c == l.GameCode() || c == l.ToString().ToLowerInvariant()) return l;
        }
        return null;
    }

    /// <summary>
    ///     Whether the game decodes the language with its Japanese/Korean/Chinese decoder (<c>Text_ToUtf8</c>
    ///     picks it for languages 0 and 9-11), else the western one.
    /// </summary>
    public static bool IsAsian(this GameLanguage language) => language is GameLanguage.Japanese or GameLanguage.Korean or GameLanguage.Chinese;

    /// <summary>
    ///     The archive path of the language's character table (<c>MallocAndReadGamecode</c>, <c>0x00646920</c>):
    ///     the western languages share <c>ffxsjistbl_us.bin</c>.
    /// </summary>
    public static string CharsetPath(this GameLanguage language) {
        string table = language switch {
            GameLanguage.Japanese => "jp",
            GameLanguage.Korean   => "kr",
            GameLanguage.Chinese  => "ch",
            _                     => "us",
        };
        return $"ffx_ps2/ffx/master/jppc/ffx_encoding/ffxsjistbl_{table}.bin";
    }

    /// <summary>
    ///     The archive path of an event's message table (<c>TKMallocAndReadEventMessage</c>, <c>0x0088cac0</c>):
    ///     <c>new_uspc/event/obj_ps3/zn/znkd0600/znkd0600.bin</c>, the folder named after its first two letters.
    /// </summary>
    public static string EventMessagesPath(this GameLanguage language, string eventName) {
        string area = eventName.Length >= 2 ? eventName[..2] : eventName;
        return $"ffx_ps2/ffx/master/new_{language.GameCode()}pc/event/obj_ps3/{area}/{eventName}/{eventName}.bin";
    }

    /// <summary>The archive path of a battle's message table: <c>new_uspc/battle/btl/cdsp00_00/cdsp00_00.bin</c>.</summary>
    public static string BattleMessagesPath(this GameLanguage language, string battleName) =>
        $"ffx_ps2/ffx/master/new_{language.GameCode()}pc/battle/btl/{battleName}/{battleName}.bin";
}
