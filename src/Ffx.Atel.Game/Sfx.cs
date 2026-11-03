// SPDX-License-Identifier: MIT

namespace Ffx;

/// <summary>
///     A sound effect, by its id: <c>Common.SfxPlay((Sfx)0x80000017)</c>. Bit 31 set is a block sound effect
///     (kind 0), clear a separate one (kind 1); the <c>SndSep*</c> functions pass <c>(kind, id &amp;
///     0x7fffffff)</c> on (ffx-rust <c>docs/formats/sound.md</c>). The ids have no names yet, so they are
///     written in hex as casts, which compile to the number.
/// </summary>
public enum Sfx : uint
{
}
