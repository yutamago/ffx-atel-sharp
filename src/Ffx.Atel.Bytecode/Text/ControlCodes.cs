// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Ffx.Atel.Bytecode.Text;

/// <summary>
///     How <see cref="Charset.Display"/> writes control codes: a marker named after what the message window
///     does with the code, from ffx-rust's <c>docs/formats/text.md</c> ("Control codes": <c>TOSetDrawInfo</c>,
///     <c>TOGetChangeMacroAddress</c>), else the code in hex (<c>{2a:31}</c>).
/// </summary>
/// <remarks>
///     Arguments that count from <c>'0'</c> (<c>0x30</c>) are written as that count. The markers are for
///     reading only; the lossless form (<see cref="Charset.Decode"/>) is what encodes back.
/// </remarks>
public static class ControlCodes {
    /// <summary>The party's default names by character number (<c>TOGetSaveChrName</c>; <c>Target.Tidus</c> 0 … <c>Magus3</c> 17).</summary>
    private static readonly string[] s_characters = [
        "Tidus", "Yuna", "Auron", "Kimahri", "Wakka", "Lulu", "Rikku", "Seymour", "Valefor", "Ifrit", "Ixion",
        "Shiva", "Bahamut", "Anima", "Yojimbo", "Cindy", "Sandy", "Mindy",
    ];

    /// <summary>The marker for control code <paramref name="code"/> with its argument (null for the codes without one).</summary>
    public static string Display(byte code, byte? arg) {
        if (arg is not byte a) {
            return code switch {
                0x01 => "{break}",
                _    => Hex(code),
            };
        }
        int n = a - 0x30;
        return code switch {
            0x07 when a is >= 0xe0 and <= 0xef => Marker("space", a - 0xe0),
            0x07 when a == 0x32 => "{right}",
            0x07 when a is >= 0x40 and <= 0xbf => Marker("tab", a - 0x40),
            0x09 when a < 0xc0 => Marker("wait", n),
            0x09 => Marker("scroll", a - 0xc0),
            0x0a => string.Create(CultureInfo.InvariantCulture, $"{{color:{a:x2}}}"),
            0x0b => Marker("button", a),
            0x0e => Marker("icons", n),
            0x10 when a is >= 0x50 and <= 0x57 => Marker("counter", a - 0x4f),
            0x10 => Marker("choice", n),
            0x12 => Marker("var", n),
            0x13 => n >= 0 && n < s_characters.Length ? $"{{name:{s_characters[n]}}}" : Marker("name", n),
            0x16 or 0x17 => Marker("item", n),
            >= 0x19 and <= 0x22 => string.Create(CultureInfo.InvariantCulture, $"{{macro:{code - 0x13}:{n}}}"),
            0x23 => Marker("keyitem", n),
            _ => string.Create(CultureInfo.InvariantCulture, $"{{{code:x2}:{a:x2}}}"),
        };
    }

    private static string Marker(string name, int value) => string.Create(CultureInfo.InvariantCulture, $"{{{name}:{value}}}");

    private static string Hex(byte code) => string.Create(CultureInfo.InvariantCulture, $"{{{code:x2}}}");
}
