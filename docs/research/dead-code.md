<!-- SPDX-License-Identifier: MIT -->
# Dead code

The original compiler kept code behind constant conditions: `if (0) { … }`
became a `jmp` over code that is still in the file. The decompiler writes it
as `if (false) { … }` (or `if (false) { … } else { … }`), and the plan keeps
every block as it is ("Decisions (user)": the dead blocks stay; "Later: find
out what each one does and make it switchable at run time"). This page is step
1 of that work, done without running the game: a catalogue of all blocks, what
they are, a few names for the ones we understand, and how a mod switches a
named block on when the loader links it (built after the catalogue).

Numbers are from `atel dead` at ffx-atel-sharp `e98c150` plus this change, over all
1,621 scripts.

## Method

`atel dead [filter] -o <dir>` decompiles every script and lists each dead
block the structurer made (the `Dead` and `DeadElse` constructs, the same
34,806 the round trip counts). For each block it writes:

- where it is: script, worker, the entries whose code holds it, the offset of
  its `jmp`;
- its size in instructions, and its depth (blocks inside other dead blocks);
- its **signature**: a hash of the instructions the `jmp` skips (constants by
  value, variables by location, jump targets relative to the block), the same
  for the same code in any worker or script (`DeadSignature`, the key of its
  name, below);
- its **live twins**: how often the same instructions also stand in live code
  of the same script. A block with a twin is a copy or a variant of code that
  runs; one without runs nowhere in its script;
- the natives it calls, the messages it names as constants (`Text[n]`), and
  which of those no live code of the script names;
- a group, by the script and the natives (`DeadCommand.Classify`).

The output stays on the player's machine (it holds game code and text):
`dead-blocks.tsv` (a row per block), `dead-summary.md` (the tables below,
per area and per script) and `dead-code.txt` (each block's C#, the live `else`
next to it, and the English text of its messages). The command takes the
corpus lock and runs in about 50 s.

What it does not see: dead code the structurer could not make an `if
(false)` (a `goto` from outside jumps into it, so it stays a `goto` over it;
the round trip reports 538 `Dead` and 100 `DeadElse` constructs dropped for
that reason), and
messages chosen at run time (`MsgSetText(0, V12)`).

## What the dead code is

34,806 blocks (19,313 `if (false)`, 15,493 `if (false) … else`) in 62
scripts, 2,461,854 dead instructions (blocks inside other dead blocks not
counted twice), 790 distinct signatures. 15,058 blocks have a live twin in
their script.

Most of it is not cut content. The scripts were written with macros, and a
macro's parameters were constants where it was used: `if (PLAYER == 0)`
became `if (0)` or `if (1)` in each copy. The compiler folded such a
condition into a `jmp` but kept both branches, so each copy of the macro
carries the branches of the other copies as dead code. The same scripts
show the parameters the compiler did not fold: `switch (2)` and `switch (6)`
on constants, with every case written out (`bltz0000`, `znkd1300`). A folded
`if (1) A else B` leaves `B` dead behind a `jmp` after `A`, so it reads as
`if (false) { B }` after the live `A`; a folded `if (0) A else B` is `if
(false) { A } else { B }`.

The blitzball scripts hold 96 % of the blocks. Four of them come in four
copies each (`bltz0000`, `bltz0005`, `bltz0006`, `bltz0201` and their `cn_`,
`psv_`, `psvcn_` variants), so without the variants it is 14,520 blocks in 50
scripts, 991,491 instructions.

## Groups

All 1,621 scripts:

| Group | Blocks | With live twin | Instructions | Scripts | Signatures |
|---|---:|---:|---:|---:|---:|
| blitzball-hud | 17,514 | 4,761 | 2,121,794 | 15 | 399 |
| blitzball-data | 6,667 | 1,207 | 227,889 | 20 | 177 |
| blitzball-message | 5,556 | 4,095 | 86,686 | 16 | 91 |
| blitzball-motion | 4,560 | 4,560 | 13,680 | 7 | 16 |
| celestial-weapon | 350 | 350 | 1,743 | 1 | 60 |
| blitzball-other | 43 | 42 | 3,762 | 10 | 4 |
| empty | 34 | 0 | 0 | 19 | 1 |
| npc-reaction | 29 | 0 | 5,713 | 1 | 10 |
| test-map | 16 | 16 | 38 | 1 | 8 |
| treasure-chest | 13 | 13 | 26 | 3 | 1 |
| battle-camera | 6 | 1 | 88 | 4 | 5 |
| scene-start | 5 | 5 | 62 | 4 | 5 |
| map-change | 4 | 4 | 49 | 4 | 4 |
| battle-actor | 3 | 2 | 119 | 3 | 3 |
| other | 3 | 2 | 105 | 2 | 3 |
| battle-line | 2 | 0 | 46 | 1 | 2 |
| dialogue | 1 | 0 | 54 | 1 | 1 |

By area:

| Area | Scripts | Blocks | Groups |
|---|---:|---:|---|
| Events.Bl_Blitzball | 19 | 33,571 | hud 17,027, data 6,636, message 5,306, motion 4,560, other 42 |
| Events.Lc_Luca | 2 | 770 | the Luca match `lchb0806` (blitzball 769), `lchb0400` 1 |
| Events.Mc_Macalania | 1 | 350 | celestial-weapon 350 (`mcfr0100`) |
| Events.Zn_Zanarkand | 1 | 29 | npc-reaction 29 (`znkd1300`) |
| Events.Te | 1 | 16 | test-map 16 (`test23`) |
| Events.Si_Sin | 2 | 12 | treasure-chest 12 |
| 23 battle areas, 2 monsters | 29 | 47 | empty 34, battle-camera 6, battle-actor 3, battle-line 2 |
| 10 other event areas | 11 | 15 | map-change 4, scene-start 5, other 3, treasure-chest 1, dialogue 1 |

What each group is:

- **blitzball-hud** (`Common.Sprite*`): the match screen's player markers,
  numbers and colours, one macro copy per player slot and team. Typical:
  `if (false) { SpriteHide(31 + (V191 + 1)); … } else { SpriteSetScale(…);
  SpriteSetPos(…); … }`, and `if (false) { SpriteSetColor(24 + (V191 + 1), 70,
  70, 70); … }` (grey) after the live colouring. Most have no exact
  twin only because the sprite numbers differ per copy.
- **blitzball-message** (`Common.Msg*`, `MacroDicText`): the score and value
  windows. 2,547 blocks are the same `MsgClose(5)` with `MsgClose(4)` live in
  the `else`: window 5 or 4 by team.
- **blitzball-motion** (`Chr.SetAutoMotion`): a player's motion per side
  (`0x600410f0` dead, `0x60041053` live). All have live twins.
- **blitzball-data** (no natives): bookkeeping per tech or slot, e.g. in
  `bltz0201` a tech's bit of `BlitzballPlayerTechsLearnedPage1` dead and its
  `Page2` bit live in the same macro copy.
- **celestial-weapon**: `mcfr0100` worker 38 entry 9, below.
- **empty**: `if (false) { }`, a `jmp` to the very next instruction, at the end
  of the `Entry(n); goto lN;` tables of 19 battle scripts. Not code.
- **npc-reaction**, **battle-line**: below; the only groups with no live twin
  besides the cameras.
- **map-change**, **scene-start**: an older copy of a map exit or scene start,
  below ("Superseded copies").
- **treasure-chest**: `Chr.SetDistanceCull(0)` in chest workers
  (`Takarabox002`) of `sins0200`, `sins0400`, `djyt0600`; the same call is live
  in other chests: a template flag.
- **battle-camera**: alternative fixed views in `hiku15_00`, `sins03_00`,
  `ssbt00_00`, `genk16_50` (`Cam.RefSetPos`, `SetPolar`, …), mostly right after
  the live view: earlier camera positions kept.
- **battle-actor**: `m147` (an attack after `return;`, copy of the live one),
  `m115` worker 2 entry 2 (picks `BattleMotion.Run00`, `RunLeft` or `RunRight`
  from the result of `Btl.ChrMoveToTarget` instead of always `Run01`),
  `system_01` worker 37 (an idle loop with the `Idle2Prob` chance; live
  twin).
- **test-map**: `test23` (a developer map), per-character flags.
- **other**: `djyt0600` worker 23 (two lift paths, up and down, one per
  macro copy), `lchb0400` (a dead `break;` that would stop story case
  `S00492_Luca0100` from falling into the next case's placement).

### Dialogue only dead code shows

None, as far as constants go. 1,878 blocks open a message window, but they
name few messages: one or two per blitzball script (`bltz0000` 11 and 12,
`bltz0002` 27 and 47, `bltz0004` 44, `bltz0005` and `bltz0006` 0, `lchb0806`
33: the value windows, `{var:0}`), `Text[38]` and `Text[39]` in `mcfr0100`
("The weapon's power has grown!", "The weapon attains its maximum power!")
and `Text[25]` in `bvyt0000` (Rikku: "Cover your eyes!"). Every one of them
is also named by live code of its script. The
lead in the plan ("dialogue only dead code shows") is not borne out for
messages named as constants; messages picked by a variable are not checked.

## Finds

### Macalania Woods: the celestial weapons (`mcfr0100`)

Worker 38, entry 9, is the spring where the Celestial Mirror powers up a
celestial weapon ("Have you the Celestial Weapons?"). It has 350 dead
blocks, among them the per-character sequences with `SetLegendAbility` the
plan mentions:

```csharp
case 1:
    Common.WaitWorkerSignal(1, 1);
    if (false)
    {
        _ = Yuna.E16Async();
        Common.SetLegendAbility(1, 1);
        Common.SetLegendAbility(8, 1);
    }
    else
    {
        _ = Yuna.E18Async();
        Common.SetLegendAbility(1, 2);
    }
    break;
```

They are not cut content. The entry is one macro with two parameters, the
character whose weapon it is and a level, written out for each of the 7
characters and both levels (14 copies). Level 1 is the crest
(for Tidus: key item 40995, `CelestialsHalfPowered`, "The weapon's power has
grown!"), level 2 the sigil (40996, `CelestialsFullPowered`, "…attains its
maximum power!"). In each copy the other level's branches are `if (false)`,
and the character shows as constant comparisons (`if
(Common.GetWorkerId(0) == Common.GetWorkerId(0))` in Tidus's copy). All 350
blocks have a live twin: the `SetLegendAbility(1, 1); SetLegendAbility(8, 1)`
above runs in the level 1 copy, next to a dead `SetLegendAbility(1, 2)`. The
second call of level 1 (8 for Yuna, 14 Auron, 10 Kimahri, 9 Wakka, 11 Lulu;
none for Tidus and Rikku) is live code too.

### Dream Zanarkand: the crowd reacts to each other (`znkd1300`)

The 24 crowd workers (`Chr035mob296_01`, `Chr053mob338_02`, …, workers 1-25
but 4) each have it in entry 8, and five also in entry 7: 29 blocks, ten
signatures (the motion and the variables differ per worker), no live twin.
Inside `if (Common.WorkerInRadius(0, 65f))` (the player is near), the dead
code loops over workers 1-25 for one within 18 units
(`Common.WorkerDistanceXZ`) and inside a 22.5° cone in front
(`Calc.FacingWithin(…, 0.3926991f, …)`); for the first it finds, it stops
moving (`Common.StopMoveAllLevels`), plays a reaction motion of its model
(a `switch` on a constant picks one of ten), waits 10 frames and looks
again. Named `Cut.ZanarkandCrowdFacingReaction`.

### Mi'ihen Highroad: two party battle lines (`system_01`)

`system_01` worker 3, entry 2 picks a party line to play in battle: a list of
`if`s, each for a story range, the party members present
(`Btl.CountActorsInBoth(Target.AllPly2, …)`) and their
`ActorProperty.TalkStat3`, which sets `V48` and the line number `V63`, marks
the line seen in `BattleDialogLinesSeen` and jumps to `Btl.RunEvent(1, 28)`.
The Highroad's lines are 330 to 337 (story progress from
`Story.S00732_Mihn0000` to before `S00777_Mihn0600`; 334 to 336 only from
`S00772_Mihn0200`). All are live but two:

- line 335 when Tidus is in the party (`V48 = 0x4bb5d504`,
  `BattleDialogLinesSeen[5]` bit 128): `Cut.HighroadBattleLine335Tidus`
  (`@d94c`);
- line 336 when Auron and Tidus are (`V48 = 0x4bb5e04e`,
  `BattleDialogLinesSeen[6]` bit 1): `Cut.HighroadBattleLine336AuronTidus`
  (`@d9a6`).

Whether the battle text and voice for 335 and 336 exist in the game files is
not checked yet (step 2).

### Superseded copies: the `AlwaysTrue_0260` gates

Seven event scripts have the same shape in a trigger entry: the live code
runs a map exit or a scene start inside `if (Common.AlwaysTrue_0260(n) == 1)`
(on PC the native drops its argument and returns 1; FFX.exe, see
`data/natives.json`) with a request in the `else` (mostly to Tidus), and the
same code once more under `if (false)` after it:

| Script | Worker, entry | `n` | Dead copy |
|---|---|---:|---|
| `ptkl0100` | 27, 4 | 2 | `MapChange(Map.Ptkl1000, 0)` |
| `swin0400` | 18, 4 | 3 | `MapChange(Map.Swin0200, 1)` |
| `kino0200` | 48, 2 and 49, 2 | 5 | the scene start (`ControlDisable`, music fade, three requests) |
| `maca0000` | 28, 4 | 7 | `TriggerSetActive(0)` and a request |
| `bika0300` | 75, 2 | 8 | `ControlDisable`, `await W39.E8Async()`, `TriggerSetActive(0)` |
| `mtgz0200` | 19, 4 | 10 | `MapChange(Map.Mtgz0300, 0)` |
| `dome0100` | 25, 4 | 11 | `MapChange(Map.Zkrn0600, 1)` (in the story case `S02850_Dome0500`) |

Read as source: the transition was there first, someone wrapped it in a check
by number, and the old lines were switched off with `if (0)` instead of
deleted. Each dead copy has its live twin in the `if`. What the check was for
(the `else` branch never runs on PC) is open.

`bvyt0000` worker 21, entry 6, case 43 is the same kind: Rikku's "Cover your
eyes!" is live as `Dialogue.SayVoiced(Text[25])`, and an older copy follows
under `if (false)` with its own `Common.VoiceStandbyEnd(0x27b2948b)` before
the line. Running it would play the line twice.

## Names

A dead block we understand gets a name instead of `false`:

```csharp
if (Common.WorkerInRadius(0, 65f))
{
    if (Cut.ZanarkandCrowdFacingReaction)
    {
        V15 = Common.GetWorkerId(1);
        …
```

- `Cut` is `Ffx.Cut` in the reference assembly (`src/Ffx.Atel.Game/Cut.cs`):
  a static class of `const bool` fields, all `false`, each with a doc comment
  that says what the block does. The compiler reads `if (Cut.Name)` (or
  `Ffx.Cut.Name`) as `if (false)`: the same `jmp`, so the vanilla source still
  compiles to the shipped bytes. A name that is not a `false` constant of
  `Cut` is an error, and a Cut name anywhere but as the whole condition of an
  `if` is one too.
- The names are keyed by the block's signature in `data/cut-names.toml`
  (`[cuts]`, `"signature" = "Name"`; `[scripts]` lists the scripts each
  name's blocks are in, for switching them on), embedded in the decompiler like
  `data/library-names.toml`. The signature does not change when the decompiler's
  C# does, nor with the worker or label numbers around it. Several signatures
  may share a name: the copies of one feature in several workers are one
  switch (the crowd's ten signatures are one name). A signature names every
  block with that code, in any script; the catalogue's copies column shows how
  far that reaches (the three named here occur only where described).
- The corpus test checks that every signature in the file still names a dead
  block, and a unit test that the file's names and the constants of `Cut` are
  the same set.
- Only blocks whose code we understand are named ("Only verified names").
  The folded macro branches (blitzball, `mcfr0100`) get no names: switching
  one on would run the other copy's branch in this copy, not hidden content.
  The superseded copies get none either: switching one on runs the transition
  twice.

Named now: `ZanarkandCrowdFacingReaction` (29 blocks), `HighroadBattleLine335Tidus`,
`HighroadBattleLine336AuronTidus`.

## Switching a block on

A named block can run without changing anything else in the script. It is
a `jmp L` (3 bytes: `b0 lo hi`) over code that ends at `L`; for `if (false)
A else B`, `A` ends in a `jmp` over `B`.

**At link time (built).** The linker replaces the block's `jmp` with
`nop_76` and the same operand (`f6 lo hi`, a no-op with an operand, 38 in
the vanilla corpus). The code is the same length, nothing moves, the label
table stays (its entry for `L` is just not jumped to from there). The block
then runs where it stands, and with an `else` its own trailing `jmp` skips
`B`: exactly `if (true)`.

A mod enables a name in its C#, in any file (a `Cuts.cs` with no namespace
is enough):

```csharp
using Ffx;
using Ffx.Atel;

[assembly: EnableCut(nameof(Cut.ZanarkandCrowdFacingReaction))]
```

Why an assembly attribute and not a `cuts = [...]` line in `mod.toml`: the
C# compiler checks the name (`nameof` of a `Cut` constant; a typo does not
build), the IDE completes it and shows its doc comment, and the switch lives
with the mod's code. `mod.toml` holds only how mods relate to each other and
is read by the loader, which would then need the names file and a scan of
the player's files to find the scripts.

What happens:

1. `atel compile-mod` (run by the SDK's build) reads the name's signatures
   and scripts from `data/cut-names.toml` (`[scripts]`: `ZanarkandCrowdFacingReaction
   = ["znkd1300"]`), counts the blocks with those signatures in the player's
   copy of each script, and adds a `cuts` entry to that script's package
   (a new package when the mod changes nothing else there):
   `{ "name": "ZanarkandCrowdFacingReaction", "signatures": [ … ], "blocks": 29 }`.
   A package with cuts has format 2, so a linker from before refuses it
   instead of dropping the switch. A name that is not in the file, or whose
   blocks the player's script does not have, stops the build.
2. The linker finds the blocks in the player's file by signature, in the
   bytecode alone (`DeadJumps` in `Ffx.Atel.Bytecode`; the loader has no
   decompiler): every forward `jmp` whose skipped code (for an `else`,
   without the last `jmp`) has one of the signatures, and which nothing
   else reaches (no jump from outside lands in it, no entry starts in it).
   It needs no offsets, so it works on any copy with the same code.
3. It checks that it found the package's number of blocks and that each
   starts with `b0 lo hi`, then writes `f6` over the `b0`. A different count
   or other bytes (another game version, a file another tool changed) is an
   error in the report, and that mod's package for the script is left out.
   Two mods that enable the same block are fine: the second finds the same
   jumps and the report says it was already on.
4. The jumps are changed in the vanilla file before any other mod's code is
   laid out, so a postfix on the same entry runs after a copy that has the
   block on. A package with only cuts gives the vanilla file with those
   jumps changed: no code moves, the file keeps its length.

Checked on the corpus (`CutCorpusTests`, through the package `atel
compile-mod` writes): each of the three names links onto the vanilla file,
at exactly the jumps the decompiler names (`HighroadBattleLine335Tidus`
`system_01` w03 `@d94c`, `HighroadBattleLine336AuronTidus` `@d9a6`,
`ZanarkandCrowdFacingReaction` 29 jumps in `znkd1300` from w01 `@03e6` to w25
`@19600`); in each 3-byte `jmp` only the opcode byte changes, and every other
byte of the file is the vanilla one. A second mod with the same name gives
the same file. The signatures occur in no other script. Not run in game
yet: what the three blocks do when they run is the next step, with the game.

Decompiling a linked file is not supported: the structurer fails on the
crowd's switched-on blocks (their loop is live then); nothing needs it, as
mods are compiled against the vanilla source.

**At run time (design, not built).** For a switch that a config or a debug UI flips while the
game runs, the linker would point the `jmp` at a stub after the code region
(as mod packages already move code there):

```
T:  pushimm <cut id>
    func 0xf002            ; a group F native the loader adds, Cut.IsOn(id)
    popa_jz L              ; off: where the jmp went
    jmp N                  ; on: the block, N a new label just after the old jmp
```

The native is registered like `Debug.Print` (`0xf000`,
[formats/debug.md](../formats/debug.md)) and reads a flag table the loader
owns. The stack is empty at every jump in the corpus, so the stub's pushes
are safe; it does write register A (`popa_jz`), which the `jmp` did not, so
the linker would have to check that no `pusha` reads A before the block's
code or `L` writes it.

What to keep in mind for both: a block's effects may be persistent
(`HighroadBattleLine335Tidus` sets a `BattleDialogLinesSeen` bit in the
save), and blocks can jump out of themselves (the crowd's loop ends in `goto
l6`, back into live code), which is fine as long as the code itself is not
changed. Next steps are in game: run the three named blocks with the
link-time switch and record what they do.
