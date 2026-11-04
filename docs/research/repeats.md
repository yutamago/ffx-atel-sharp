<!-- SPDX-License-Identifier: MIT -->
# Repeated code

The game's scripts repeat themselves. A line of dialogue is the same 18
statements with a different message; a blitzball script writes the same
sprite code once per player slot. The plan's decision of 2026-11-04 is that
such code is written once, as an `[Inline]` helper the compiler writes out at
each call (as `Dialogue.Say`), so the bytes stay the same. This page finds
the candidates, measures what they would save, checks that each one gives
the shipped bytes back, and proposes names and signatures for the first
ones. Since 2026-11-04 the decompiler folds them ([Status](#status)).

Numbers are from `atel repeats` at ffx-atel-sharp `76a2cb8` plus this
change, over all 1,621 scripts.

## Method

`atel repeats [filter] -o <dir>` (`src/Ffx.Atel.Cli/RepeatsCommand.cs`,
`Repeats/`):

1. **Statements.** Every script is decompiled up to the structured
   statements (`ScriptDecompiler.Structured`, new; the same statements
   `atel decompile` writes, before any inline method is folded). A statement
   is the code from an empty stack to the next empty stack, so it is a fixed
   instruction sequence: putting the same values back into it gives the same
   bytes.
2. **Shapes.** Each statement's tree is reduced to a shape: every constant
   (and with `--vars` every variable) becomes a hole, and the value is kept
   aside. A statement inside an `if`, loop or `switch` counts by its own
   shape, so whole blocks compare as one number. Natives, operators, case
   labels, `if (false)` and `Cut` names stay in the shape.
3. **Runs.** A candidate is a run of consecutive statements of one block
   (the body of a method, an `if` branch, a loop, a `case`) that recurs with
   the same shapes. It never crosses a block or statement boundary the
   decompiler makes, because an inline method's body is a list of whole
   statements. Statements no helper can hold end a run: jumps, labels,
   `Asm`, `Entry(n)`, `return`, and a `break`, `continue` or `goto case`
   whose loop or `switch` is outside the run (C# cannot leave the caller's
   loop from a method). Code in dead `if (false)` blocks is not mined (the
   decompiler folds nothing there), but a run may hold a whole dead block.
   The runs are found by splitting the places of each shape by the shape
   that follows, one statement at a time (a suffix-tree walk, at most 48
   statements), keeping runs that are maximal on both sides.
4. **Cover.** A greedy cover takes the run that saves the most lines, keeps
   only the places no run taken before uses, and repeats. Saved lines are
   `places × (lines − 1) − lines`: each place becomes one call, and the
   helper's body is written once. The first place stays in the cover as
   that body, so a smaller helper can still be found inside a larger one.
5. **Parameters.** The holes whose value differs between the places are the
   parameters; holes that always hold the same value as each other are one
   parameter used several times; holes with one value everywhere stay
   constants in the body. Helpers with more than 8 parameters are dropped
   (`--max-params`).
6. **Copies.** A worker with the same code as one mined before
   (`WorkerSignature`, the key the library uses to share workers; the
   `cn_`, `psv_`, `psvcn_` variants) is skipped: 3,525 workers corpus-wide.

The miner checks **exactness** three ways. The body of each helper is its first
place with parameters in the holes; `Inline.Match` must find the place again
and `Inline.Expand` give it back (the "self check", true for every helper
below). With `--verify n` the first `n` helpers become inline methods for
the real decompiler, next to the reference assembly's: `FoldInline` folds
them, lowers the worker again and compares it with the original instruction
by instruction, labels and entries included, and drops the folds of any
worker that does not compare. Over every run on this page no fold was
dropped (`inline-fallback-verify` 0). This is a check on the statements, not
on C# text: most blitzball helpers cannot be written as C# inline methods
yet (see [What the language needs](#what-the-language-needs)).

The output stays on the player's machine (it holds game code):
`repeats.tsv` (a row per helper), `repeats.md` (the tables below and each
helper's first place as C#, its parameters, their values),
`repeats-code.txt` (each helper's body in full) and `verify.tsv` (lines per
worker with and without the helpers). The whole corpus takes about six
minutes with 4 jobs and 2.5 GB, nearly all of it the blitzball scripts;
everything else takes 30 s.

Lines are counted two ways: the miner's estimate (as the decompiler would
write the statements, before `Dialogue` is folded) and, for `--verify`, the
lines the decompiler really writes with and without the helpers.

## Results

| scripts | statements | lines (miner) | helpers | lines saved (miner) | lines written (decompiler) before → after | with the helpers' bodies |
|---|---|---|---|---|---|---|
| all 1,621 | 4,580,259 | 6,451,930 | 60 | 3,026,663 (46.9 %) | (checked in the two parts below) | |
| blitzball (24) | 2,579,477 | 3,947,088 | 40 | 2,241,825 (56.8 %) | 3,932,633 → 1,625,815 (−58.7 %) | −57.1 % |
| the rest (1,596) | 1,931,938 | 2,402,519 | 60 | 819,299 (34.1 %) | 2,581,887 → 2,025,753 (−21.5 %) | −21.5 % |
| `bltz0000` alone | 377,249 | 580,476 | 30 | 471,229 (81.2 %) | 581,207 → 91,500 (−84.3 %) | −79.9 % |

(Distinct workers only. "Before" already has `Dialogue` folded; the
decompiler's line count of a helper's body, written once, is at most its
lines at the first place.)

The blitzball scripts are 56 % of the corpus's statements and most of its
repetition. Corpus-wide, their helpers take 49 of the first 60 places, so
the rest of the game is mined on its own (`--no-blitzball`) for the general
list below.

### Blitzball

`Bltz0000`'s worker 36 is one method of 190,414 lines in 15.7 MB.
Mined on its own (`atel repeats bltz0000/bltz0000 --top 30 --verify 30`):

| W36 of `bltz0000` | statements | lines |
|---|---|---|
| as decompiled today | 121,398 | 190,414 |
| with the 30 helpers mined from `bltz0000` | 9,432 (miner) | 13,763 (decompiler) |
| with the 40 helpers mined from all blitzball scripts | 28,147 (miner) | 38,268 (decompiler) |

What W36 is made of, in the ranks of the `bltz0000` run: seven copies of one
19,756-line block (R002: 12,337 statements and no parameter, the same code
seven times; 138,000 of W36's lines), a column of eight `SpriteHide` 7 apart
(R014, 330 places), the ten-entry list in `V163` scrolled up by two with a
request to W19 (R019, 128) and its new entry written into message window 6
(R011, 128), and window 6 opened or closed by `V214` (R025, 90). The helpers'
bodies add at most 25,356 lines, once for the whole script. W36 of `Bltz0002`
is the same size with the same seven-copy block.

The greedy order matters here: mined over all blitzball scripts, the sprite
helpers that occur in every script come first, and their places lie inside
`bltz0000`'s seven copies, so the large block is not taken there and W36
keeps 38,268 lines. A cover that weighs whole blocks first, or mines each
large worker on its own first, would keep both.

Across the 24 blitzball scripts (`atel repeats bltz --top 40 --verify 40`),
the first helpers, named after what the code does (the names are
proposals; the sprite numbers are the match HUD's slots, as in
[dead-code.md](dead-code.md)):

| # | places | scripts (w/o variants) | statements (size) | lines saved | helper | does |
|---|---|---|---|---|---|---|
| R001 | 1,464 | 4 (1) | 3 (85) | 215,060 | `Hud.ShowStatIcon(stat, slotBase, …)` | `V145 = 0; switch (stat) { case k: SpriteShow(slot, icon_k); V108 = statValue_k; V145 = 1 or 2 by the buff and debuff arrays … }`: the selector is a constant, the macro parameter the compiler did not fold |
| R002 | 2,088 | 4 (1) | 4 (66) | 212,873 | `Hud.ShowTwoDigits(slotBase, scale, x, …)` | scales and places a sprite, then `switch (V108 / 10)` and `switch (V108 % 10)` pick the digit images (`0x78000180 + 0x60·d`), and `switch (V145)` its colour |
| R004 | 156 | 1 | 19 (653) | 164,299 | `Hud.DrawPlayerPanel(slot)` | a player's panel: icon sprites scaled and placed, the stat `switch` on constants (`switch (0)`, `switch (6)`), and dead `if (false)` branches of the other team |
| R005 | 7 | 1 | 27 (12,337) | 118,529 | (W36's block) | seven identical copies in W36, no parameter |
| R007 | 4 | 2 (1) | 1 (18,030) | 102,833 | `Ai.PickTechs(allowed)` | `bltz0201` W06: a 34,279-line `switch`; its one parameter is a 0/1 the compiler left in 649 conditions (`if (false & V102 < V165[50])`) |
| R018 | 5,618 | 19 (8) | 8 | 39,318 | `Hud.HideColumn(s0, …, s7)` | `SpriteHide` of 8 sprites 7 apart (45, 52, … 94): 8 parameters for 8 lines, no gain unless the compiler folds `base + 7·k` |
| R019 | 19,497 | 11 (6) | 3 | 38,991 | `Motion.SetUnblended(motion)` | `Chr.SetMotion(m); Chr.SetMotionBlend(0); Chr.SetNextMotionBlend(0);` |
| R020 | 3,800 | 7 (4) | 4 (6) | 37,989 | `Chr.LoadAliasAutoMotions(model, …)` | `ChrLoadAlias`, then `if (false) { SetAutoMotion(16, a) } else { SetAutoMotion(16, b) }` and slots 18, 19: a folded team branch inside the body |
| R027 | 1,598 | 4 (1) | 16 | 23,954 | `Hud.SetValueRow(window, …)` | ten `MsgSetValue` of window 6 from `MacroDicText` (assigns `V101`) |
| R028 | 7,895 | 20 (9) | 4 | 23,681 | `Dialogue.OpenInPlace(text, window, …)` | `Dialogue.Open` without `MsgSetPos`: the window keeps its place |

The blitzball helpers read and write the script's variables (`V145`,
`V108`, the stat arrays) and request other workers; of the first 40, all but
four (R018, R019, R020, R028) need a language extension before they can be
written in C#. The 40 helpers hold 796 dead `if (false)` blocks in their
bodies, standing for 11,143 at the places they fold.

### The rest of the game

`atel repeats --no-blitzball --top 60 --verify 60`. The 60 helpers fold
256,043 places; every fold passes the decompiler's check. Proposed names
follow the object-oriented surface (`Turn`, `Move`, `Motion` are members of
the worker; `Cam`, `Dialogue`, `Map` stay static); they describe what the
natives do, nothing more.

| # | places | scripts | lines saved | helper | stands for | needs |
|---|---|---|---|---|---|---|
| R001 | 32,456 | 329 | 129,819 | `Cam.OrbitEased(target, a2, a3, a4, a5, depth: 600f, accel, decel, curve)` | `Cam.EyeOrbit(target, -1, a2…a5); Cam.SetRoll(0f); Cam.SetScreenDepth(depth); Cam.MoveEased(accel, 0, decel, curve); Cam.FocusTimeEase(` the same `)` | `target` (`V1`) as a parameter |
| R002 | 5,281 | 192 | 121,439 | `Dialogue.SayWithVoice(voice, text, …)` | `Common.VoiceStandbyEnd(voice); Common.WaitFramesUnlessHddMode(5);` then `Dialogue.SayVoiced(text, …)` | nothing |
| R003 | 6,695 | 140 | 120,491 | `Dialogue.Say` | exactly the existing macro (the miner finds it on its own) | nothing |
| R004 | 23,653 | 561 | 70,955 | `Cam.SetRollAndDepth(roll, depth)` + `Cam.WaitAll()` | `Cam.SetRoll; Cam.SetScreenDepth; Cam.Wait(); Cam.FocusWait();` | nothing |
| R005 | 7,329 | 288 | 51,295 | `Move.WalkTo(x, y, z, turnSpeed: 0.31415927f)` | `TurnSetSpeed; TurnSetSpeed2(0.1745f); MoveSetTarget(x, y, z); TurnSetYaw(GetYawToMoveTarget()); TurnStart(0, 53250, 0); MoveStart(0, 49153, 0); MoveWait(); WorkerStopTurn(GetWorkerId(-1));` | nothing |
| R006 | 41,746 | 831 | 41,744 | `Cam.SetRollAndDepth(roll, depth)` | `Cam.SetRoll(roll); Cam.SetScreenDepth(depth);` | nothing |
| R007 | 7,766 | 280 | 31,059 | `Dialogue.Open` | exactly the existing macro | nothing |
| R008 | 5,336 | 234 | 26,674 | `Turn.Animated(small: 15, large: 30)` | `I3 = GetTurnKind(); Chr.SetMotion(GetTurnMotion(I3)); TurnSetFrames(0f, GetTurnFrames(I3, small, large)); TurnStart(0, 4099, 0); TurnWait(); Chr.ReleaseMotion();` | a local (`I3`) |
| R009 | 16,438 | 560 | 16,436 | `Cam.Ease(accel, hold, decel, curve, …)` | `Cam.MoveEased(…); Cam.FocusTimeEase(…);` | nothing |
| R012 | 9,406 | 642 | 9,404 | `Cam.Place(rx, ry, rz, x, y, z)` | `Cam.RefSetPos(rx, ry, rz); Cam.SetPos(x, y, z);` | nothing |
| R014 | 493 | 129 | 7,871 | `Voice.PlaySkippable(voice)` | the voice of `SayWithVoice` without a window: standby, start, a loop that stops it on button 32, `SoundWaitPoll` | nothing |
| R015 | 2,612 | 202 | 7,832 | `HideAndDisableTriggers()` | `DisableTrigger(0); DisableTrigger(1); Chr.SetBodyHit(1); Chr.Hide(1);` | nothing |
| R016 | 7,330 | 317 | 7,328 | `Move.StopAndFace(yaw)` | `WorkerStopMove(GetWorkerId(-1)); SetYaw(yaw);` | nothing |
| R017 | 7,253 | 328 | 7,251 | `Cam.PlacePolar(rx, ry, rz, theta, phi, len)` | `Cam.RefSetPos(…); Cam.SetPolar(…);` | nothing |
| R020 | 1,925 | 180 | 5,771 | `ShowAndEnableTriggers()` | the reverse of R015 | nothing |
| R021 | 1,738 | 117 | 5,210 | `Dialogue.PlayVoiceAndClose(window)` | `VoiceStartEnd(); SoundWaitPoll(); MsgClose(w); MsgWaitExEnd(w, 1);` | nothing |
| R022 | 71 | 1 | 5,179 | `Crowd.React(kind)` | `znkd1300`'s crowd: `StopMoveAllLevels(); switch (kind) { case k: if (GetMotion() == m_k) break; PlayMotion(m_k, …); … }`, a constant selector | nothing |
| R023 | 1,592 | 168 | 4,772 | `Move.TurnAndMove(turnMode, moveMode, param: 0)` | `TurnStart; MoveStart; MoveWait(); WorkerStopTurn(GetWorkerId(-1));`, the tail of R005 | nothing |
| R024 | 134 | 115 | 4,388 | `Party.RestoreAll()` | `CharRestoreHp` and `CharRestoreMp` of characters 0-17 (34 calls, no parameter): the save spheres | nothing |
| R025 | 2,106 | 95 | 4,209 | `Move.InFrames(frames, mode)` | `MoveSetFrames(0, frames); MoveStart(0, mode, 0); MoveWait();` | nothing |
| R027 | 394 | 56 | 3,929 | `Btl.SubtitleAndWaitVoice(line)` | `if (GlobalBit9Guarded()) { MsgDrawEvjimaku(0, line); MsgWaitCloseStream(0); MsgCloseWindow(0); if (VoiceIsStreaming()) SndStopStream(); } VoiceStreamWaitPoll();` | nothing |
| R031 | 614 | 97 | 3,677 | `Savesphere.OpenMenu()` | `SetFieldFlag08(0); FadeOut(15); MsgWaitExEnd(2, 1); WaitFrames(15); MenuOpenResult(0x40200000); FadeIn(15); SetFieldFlag08(1);`, no parameter | nothing |
| R035 | 1,651 | 290 | 3,299 | `Map.ChangeFaded(map, entrance)` | `SetMapFadeFrames(15); SetMapFadeType(0); MapChange(map, entrance);` | nothing |
| R039 | 306 | 96 | 2,744 | `Savesphere.JumpToBltz0200(effect)` | `SfxPlay`, `FpBind`/`FpRun(effect)`, a fade, `MapJump(Map.Bltz0200, 0)` | nothing |
| R046 | 746 | 130 | 2,234 | `Turn.To(yaw, frames, mode: 4099)` | `TurnSetFrames(0f, frames); TurnSetYaw(yaw); TurnStart(0, mode, 0); TurnWait();` | nothing |
| R051 | 484 | 16 | 1,931 | `Chr.LoadModelWithTurns(group, model, m0, m1, m2, m3)` | `LoadSystemMgrpResult; WaitCdIdle(); SetModel(model); SetTurnMotions(…); SetPushWeight(-1);` | nothing |
| R053 | 899 | 104 | 1,795 | `Motion.PlayOnceThenLoop(once, loop, …)` | `PlayMotion(once, 0, 1, …); WaitMotion_501e(); PlayMotion(loop, 0, 0, …);` | nothing |
| R060 | 1,595 | 280 | 1,593 | `Trigger.SetLine(x1, y1, z1, x2, y2, z2, extend1, extend2)` | `TriggerSetLine(…); LineExtend(…);` | nothing |

Left out of the list though the cover takes them: runs that are no clearer
as one call (R011 and R026, two requests; R013 and R030, `Btl.DebugPrint`
runs; R010, a `WinSe` property and `I1 = Random16() % 3`; R042, three
`SetActorProperty` whose properties differ too; R058, `SetNextMotionBlend`
before `PlayMotion`, which the object-oriented `Motion.Play(…, blend:)`
covers), and the battle scripts'
setup block (R019, 314 places, one per battle script, reading and writing
`V0`…`V5`), which the library would share as a method of a battle base
class rather than an inline helper.

**The turn the plan names** (`Turn.To(yaw, minFrames, maxFrames)`) is R008
plus the statement before it: all 6,048 `GetTurnKind` sequences in the
decompiled source follow a `TurnSetYaw(…)`. The miner leaves that
statement out because its yaw is usually a native call
(`AngleToPoint(GetWorkerX(0), …)`, `GetYawToMoveTarget()`), and an inline
argument may not hold a call. With the extension below the helper is

```csharp
[Inline(Waits = true)]
public void To(float yaw, int small = 15, int large = 30) {   // Turn.To(Common.AngleToPoint(…));
    Common.TurnSetYaw(yaw);
    int kind = Common.GetTurnKind();            // lowers to I3, as the game's code has it
    Chr.SetMotion(Common.GetTurnMotion(kind));
    Common.TurnSetFrames(0f, Common.GetTurnFrames(kind, small, large));
    Common.TurnStart(0, 4099, 0);
    Common.TurnWait();
    Chr.ReleaseMotion();
}
```

### Overlaps

- **Dialogue.** The miner finds the existing macros by itself: R003 is
  `Dialogue.Say` and R007 `Dialogue.Open`, statement for statement, and in
  `--verify` they fold nothing because the reference assembly's macros
  match those places first. What it adds around them: the voice prepared
  before a voiced line (R002, `VoiceStandbyEnd` and
  `WaitFramesUnlessHddMode(5)` then `SayVoiced`: 5,281 places), the voice
  without a window (R014), the voiced tail without the loop (R021), and in
  blitzball `Open` without `MsgSetPos` (R028). These belong in `Dialogue`.
- **Library.** `Library/Shared*.cs` shares whole workers that are the same
  in several scripts; the miner skips those copies (step 6) and works inside
  methods. What it finds across scripts is code that recurs inside
  different workers. One run is a library case after all: the battle setup
  block (R019) is the first statements of entry 5 in every battle script.

### Variables as parameters

With `--vars` a variable is a hole too, so `V63[V192]` and `V57[V192]` are
the same shape. For blitzball it does worse: the cover then takes small
helpers that occur everywhere (`if (a > 0) { V145 = 1; } if (b > 0) { V145 = 2; }`,
49,225 places) and
they break up the large macros, so the 40 helpers save 37.2 % instead of
58.7 %, and W36 goes to 97,281 lines instead of 38,268. Read as code those
small helpers are no clearer either (`SetIfPositive(ref V145, V73[V192],
1)`). Constants as parameters is the better default; a variable that is the
same at every place but differs between scripts (`Cam.EyeOrbit(V1, …)`) is
made a parameter by hand.

## Dead branches and macro parameters

The dead blocks are mostly the original compiler's folding of a macro's
constant parameter ([dead-code.md](dead-code.md)): `if (PLAYER == 0) A else
B` became `A; if (false) { B }` with the condition true and
`if (false) { A } else { B }` with it false. Three findings:

1. **Dead blocks inside a helper stay, byte for byte.** A run may hold whole
   `if (false)` blocks; the helper's body then holds them, written once
   instead of at every place. In `bltz0000`, 2,931 dead blocks are at the
   places the 30 helpers fold, and 140 are in their bodies. Every one of
   those folds passes the decompiler's check, so keeping every dead block
   (the plan's decision) and folding them into helpers go together. They stay
   visible in the helper's body, not at the call; `Cut` names are not
   affected (the folded macro branches get none, per dead-code.md).
2. **When the constant survived, it is a parameter already.** Where the
   original compiler did not fold the parameter, it is a constant in the
   code and the miner makes it an argument: R001 in blitzball
   (`switch (stat)` on a constant, 8 values), R007 in `bltz0201` (`false &
   …` in 649 conditions, one parameter with values 0 and 1), R022 in
   `znkd1300` (`switch (6)`).
3. **When it was folded, the two foldings are two shapes**, so today they
   become two helpers: the body differs in where the `jmp` goes. Counted by
   shape (`DeadTwins`):

   | scripts | `if (false) { A } else { B }` | with a twin `A; if (false) { B }` | `A; if (false) { B }` | with a twin |
   |---|---|---|---|---|
   | all | 13,878 | 9,745 | 11,432 | 3,576 |
   | blitzball | 13,696 | 9,569 | 11,186 | 3,400 |
   | other | 182 | 176 | 246 | 176 |

   So 70 % of the false foldings have the true folding of the same `A` and
   `B` elsewhere. To make that constant an argument, the compiler would need
   a constant `if` on a parameter, lowered as the original compiler did:

   ```csharp
   [Inline]
   static void SetTeamMotion(int team, int motion0, int motion1) {
       if (team == 0) { Chr.SetAutoMotion(16, motion0); }   // with team constant:
       else { Chr.SetAutoMotion(16, motion1); }              // true  → A; if (false) { B }
   }                                                         // false → if (false) { A } else { B }
   ```

   Both forms already compile to the shipped bytes today (the decompiler
   writes them), so the expansion would be one of two known statement
   lists. Not checked yet: that the labels come out in the original's order
   when the constant `if` starts before `A` rather than after it. The dead
   branch would then read as the other side of an `if` in the helper, with
   no `if (false)` at the call.

## What the language needs

Today an inline method calls natives and inline methods with `if`, loops
and `switch` around them; it assigns nothing, declares no locals, uses only
its parameters and constants, and its arguments hold no calls
([language.md](../language.md#inline-methods)). What the helpers above need
beyond that, from the `needs` column of `repeats.tsv`:

| need | helpers | what it would take |
|---|---|---|
| nothing | most of the general list, `Dialogue.*`, R018-R020, R028 in blitzball | write them now |
| fields | most blitzball helpers, `Cam.OrbitEased` | read the script's variables in the body; or a parameter, which may be a variable today |
| assign | blitzball (`V145 = 1`, `V101 = MacroDicText(…)`) | assign the script's variables in inline methods of the script class; `ref` parameters where the variable differs between places |
| locals | `Turn.Animated`/`Turn.To` (`I3`) | a local in the inline method that lowers to the register the game used |
| requests | blitzball's shot sequences (R010, R013 of that run) | requests and `WaitForEntry` in inline methods |
| call arguments | `Turn.To(Common.AngleToPoint(…))` | a call as argument when its parameter is used once, in the first statement, before anything else runs |
| constant `if` | the 9,745 folded twins | the lowering above |

Two refinements the miner could add: parameters that move together (the
eight sprite numbers of R018 are `base + 7·k`; one parameter if the
compiler folds `base + 7` after substitution), and defaults (each
parameter's most common value is in `repeats.md`, e.g. 448 for 6,690 of the
6,695 `Dialogue.Say` places).

## Next steps

1. The `Dialogue` additions and the general helpers that need nothing new,
   as inline methods of the reference assembly; check each with `atel
   roundtrip --check` and count the folds.
2. The language extensions in the order the table above saves lines:
   assignment and fields (blitzball), then locals and call arguments
   (turning), then the constant `if`.
3. Blitzball helpers belong to the blitzball scripts, not the reference
   assembly: the decompiler would need inline methods from the source repo
   (`InlineMethods.With(trees)` already reads them from C#).

## Status

2026-11-04: Phase 1, byte for byte. What was built, against the plan above:

- **The language gaps** ([language.md](../language.md#inline-methods)): an inline method may read and
  assign the script's variables and its worker's registers (the blitzball macros), request entries
  and call function workers, hold opcode statements, and be `async Task` when it awaits a request (a
  call is `await Name(…);`: W36's big block awaits W18). `[InlineLocal("kind", 3)]` keeps a local in
  register `I3` (the turn), and an `[Eager]` parameter, used once in the first statement, takes a call
  (`Turn.To(Move.YawToTarget)`). The analyzers know an inline call is no request (ATL2005, ATL2008).
- **General helpers** in the reference assembly (`src/Ffx.Atel.Game/Inline/`): `Turn.To` (R008 with
  its `TurnSetYaw`, 5,148 calls in the decompiled corpus), `Turn.InFrames` (R046, 1,037), `Move.To`
  (R005, 7,475), `Move.StartWithTurnAndWait` (R023, 1,651), `Move.InFrames` (R025, 2,210),
  `Dialogue.SayWithVoice` (R002, 5,388), `ChangeMap` (R035, 2,087) and `JumpToMap` (1,357),
  `Party.RestoreAll` (R024, 137). Left out: camera runs whose arguments are not described yet
  (R001, R004, R006), and two-line runs that read as well written out (R016).
- **Each worker's own repeats.** The miner moved into the decompiler (`Repeats/`) and runs per worker
  after the reference assembly's methods are folded, so `Dialogue.Say` and `Turn.To` are single
  statements to it. Per worker rather than per script or per family: a helper names the worker's
  registers and the script's variable numbers, it travels with the worker into the library, and the
  cover of one worker is not fragmented by small helpers that recur elsewhere (the problem of
  "Blitzball" above): W36's seven-copy block is the cover's first helper. A run needs 8 lines, 100
  lines saved and at most 6 parameters; shorter ones read better written out than as a call of an
  unnamed method. Bodies are folded with the helpers taken after them; each worker is lowered again
  and compared, and keeps nothing if it differs (no worker did).
- **Names**: `RepeatedNN`, or a name from `data/inline-names.toml` by the body's signature (11
  signatures today: `WinPoseCamera` in 1,010 battle workers, `ShowStatIcon`, `ScrollListAndAdd`,
  `HidePlayerPanel`, `ShowTwoDigits`, `LoadAliasAutoMotions`, `WaitForMotionFrame`, …).
- **The constant `if` on a parameter** is not done: the two foldings of one macro stay two helpers.
  It needs the expansion to give the original's label order where the constant `if` starts before
  `A`, which nothing checks yet.

| | before | after |
|---|---|---|
| W36 of `bltz0000` | 190,402 lines | 15,447 lines (its helpers included) |
| the decompiled corpus (`.cs` lines) | 7,842,894 | 4,403,971 (−43.8 %) |
| the blitzball area | `Blitzball.slnx`, a project per script | one area project, 57 MB |
| worker helpers | | 3,617 methods (232 `async Task`) in 667 script folders |
| `atel roundtrip --check` | 1,621 / 1,621 | 1,621 / 1,621, 0 C# errors, 221 warnings (ATL2006) |

