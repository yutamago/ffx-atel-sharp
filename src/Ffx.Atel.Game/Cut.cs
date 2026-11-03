// SPDX-License-Identifier: MIT

namespace Ffx;

/// <summary>
///     Names for dead code: <c>if (Cut.ZanarkandCrowdFacingReaction) { … }</c> is <c>if (false) { … }</c>, code
///     the original compiler kept behind a <c>jmp</c> that the game never runs (docs/research/dead-code.md).
///     Each name is a constant <c>false</c>, so the source compiles to the shipped bytes; the decompiler
///     writes it for the blocks <c>data/cut-names.toml</c> names, by the signature of their code. Only blocks
///     whose code is understood get a name.
/// </summary>
/// <remarks>
///     A name is a switch: a mod that writes <c>[assembly: EnableCut(nameof(Cut.ZanarkandCrowdFacingReaction))]</c>
///     has the linker run the named blocks (and skip their <c>else</c>) without touching the rest of the script
///     (docs/research/dead-code.md, "Switching a block on").
/// </remarks>
public static class Cut
{
    /// <summary>
    ///     Dream Zanarkand (<c>znkd1300</c>): the crowd workers 1-25, in their entries 7 and 8 while the player is
    ///     within 65 units, look through workers 1-25 for one within 18 units and inside a 22.5° cone in front
    ///     (<c>Calc.FacingWithin</c>); then they stop moving, play a reaction motion of their model (a
    ///     <c>switch</c> on a constant picks it), wait 10 frames and look again. 29 blocks in 24 workers.
    /// </summary>
    public const bool ZanarkandCrowdFacingReaction = false;

    /// <summary>
    ///     A party battle line on the Mi'ihen Highroad (<c>system_01</c>, worker 3, entry 2; story progress
    ///     <c>Story.S00772_Mihn0200</c> up to <c>Story.S00777_Mihn0600</c>): when Tidus is in the party and his
    ///     <c>ActorProperty.TalkStat3</c> is set, line 335 (<c>V63</c>), marked seen in
    ///     <c>BattleDialogLinesSeen[5]</c> bit 128, as the live lines of the same list do.
    /// </summary>
    public const bool HighroadBattleLine335Tidus = false;

    /// <summary>
    ///     A party battle line on the Mi'ihen Highroad (<c>system_01</c>, worker 3, entry 2), as
    ///     <see cref="HighroadBattleLine335Tidus"/>: when Auron and Tidus are both in the party with
    ///     <c>ActorProperty.TalkStat3</c> set, line 336, marked seen in <c>BattleDialogLinesSeen[6]</c> bit 1.
    /// </summary>
    public const bool HighroadBattleLine336AuronTidus = false;
}
