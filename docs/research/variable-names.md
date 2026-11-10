<!-- SPDX-License-Identifier: MIT -->
# Variable and entry names by rule

What the decompiler names beyond the save (`Save`, [save-fields.md](save-fields.md)) and
the battle handlers ([handlers.md](handlers.md)), and what each rule was checked against.
The rules are in `src/Ffx.Atel.Decompiler/Naming/` (`EntryRoles`, `VariableRoles`,
`WorkerNaming.FunctionRole`) and `Layout/SharedWorkers.cs` (library classes); the table
of the shared variables in `data/variable-names.toml`. Counts are from `atel decompile all`
of the 1,621 scripts (2026-11-09, ffx-atel-sharp `8e0a942` and this change).

## Storage 1 (`Storage.Common`)

Storage 1 is the ctrl work's `+0x30` area (`AtelGetRealVarAdrs`, ffx-rust
`docs/formats/script.md`, "Variables"): one area the scripts running on one ctrl work
share. No event script declares it; 545 battle scripts and the monsters do (7,826
declarations, 101 offsets). The names are global (`data/variable-names.toml`), so an
offset reads the same in every script. Named where every writer and reader in the
corpus agrees (each store and each read of the offset, grouped by shape, over all the
scripts that declare it):

| Offset | Name | Scripts | Writers | Readers |
|---|---|---|---|---|
| `0x0030` | `CameraActor` | 567 | `Btl.GetTurnActor()` (547), `Btl.FindActor(Target.AllPly, …)` (547, while an aeon is out), copies (534) | the actor of `Camera.Eye.Orbit` and `Camera.Focus.OrbitScaled` (116,000 reads) |
| `0x002c` | `TurnActorIsParty` | 545 | 0, then 1 where `GetTurnActor()` is 0-19 (`Target.Tidus` … `Dummy2`; monsters are `0x14` on) | conditions of the camera handler |
| `0x0038` | `CameraActorUnavailable` | 545 | 0, then 1 where `CameraActor` is not in battle, stoned, about to die, flying or not alive (`ActorProperty.Inbattle`, `Stone`, `WillDie`, `Fly`, `Alive`) | conditions |
| `0x003c` | `AeonOnField` | 548 | 0, then 1 where `Btl.CountActorsInBoth(Target.AllPly, Target.AllSummon)` is not 0 | conditions |
| `0x0094` | `MoveSteps` | 169 | `Btl.ChrGetMoveSteps(Target.Active)` (148 of 222) | `Wait.Frames(X - n)`, `Camera.Eye.Move(X)`, `Camera.Focus.Move(X)` |
| `0x00f4` | `LastCameraPick` | 132 | `X = I0` after `do { I0 = Cam.BtlRandom(0) % n; } while (I0 == X);` | that loop, `switch (X)` |

Left unnamed: `0x0034` (`+= 1; &= 1` in 538 scripts, never read), `0x0040` (1 when
`CountActorsInBoth(AllPly2, AllPly2) == 1`; what `CHR_ALL_PLY2` picks is not known),
`0x0044`-`0x004c` (zeroed, then copied into `F0`-`F2`), `0x0174` and `0x025c` (a mode
the system battle `system_01` sets by the party's HP and an actor that overrides
`CameraActor` in mode 3; the writers are too few to say more), and the offsets below 100
scripts.

## Script variables (storage 2, 3, 4, 6)

`VariableRoles` names a script's own variables that still read `V12` by what the code
does with them. Not the save (storage 0) and not storage 1: those are shared, and a name
from one script's code would differ from another's.

- **`TriggerEntries`**: a variable only ever used as the table of
  `SetTriggerEntry(Addr(ref X[0]), kind, entry)` (`op_79`, which copies the worker type's
  default trigger entries into it and points the worker's `+0x44` at it; ffx-rust
  script.md, "Trigger entries").
- **A native's result**: a plain variable that is read, every store to which is the
  result of one native, is named after it without `Get`: `MsgChoiceResult`,
  `MotionFrame` (`Chr.GetMotionFrame`). The value is what the native returns; nothing
  else writes it.

A name the script already has gets `_2`, `_3`, … in table order. Before: 25,762
variables named `V<n>`; after: see the plan's numbers.

## Entries

`EntryRoles` names the entry methods the overlay and the handler table leave numbered.

**Trigger entries.** The engine requests a trigger's entry from a table per worker type,
copied by `SetTriggerEntry` into the worker's own table on first use. Read from FFX.exe
(entries `0x00c52ad8`, levels `0x00c52ac0`, eight `u16`/`u8` per row; checked against
the file for this change and against ffx-rust's table):

| Type | Entries by kind 0-7 | Levels |
|---|---|---|
| 1 (`Actor`), 4, 0 | `2 3 4 5 - - - 1` | `7 7 6 5 - - - -` (only type 1 has checks) |
| 2, 3 (`LineTrigger`, `BoxTrigger`) | `2 3 - 5 4 6 7 1` | `7 7 - 3 5 4 2 -` |
| 5 (`LineSensor`) | `2 2 - 2 2 2 2 1` | `1 1 - 1 1 1 1 -` |
| 6 (`BoxSensor`) | `2 2 - 2 2 2 3 1` | the same |

Kinds (ffx-rust): 0 talk, 3 touch, 4 a line or box crossed, 5 a touch began (or a
crossing without a touch), 6 a touch ended (or after such a crossing); 1 and 2 are not
identified and name nothing. A request is made only when the worker's `+0xab` has the
kind's bit: `0xf7` for an actor at start (so no touch until `EnableTrigger(3)`), `0xff`
for types 2, 3, 5, 6; `EnableTrigger`/`DisableTrigger` (`0x0034`/`0x0035`) set and
clear it; only an actor bound to a model is talked to (`Atel_CheckTouchTalk`). So:
`OnTalk` (entry 2 of an actor that loads a model and does not turn talking off for good,
of a line or box trigger), `OnTouch` (entry 5: an actor that turns touch on, a trigger),
`OnCross` (entry 4 of a trigger, entry 2 of a line sensor), `OnTouchBegin`, `OnTouchEnd`
(entries 6, 7 of a trigger; 2, 3 of a box sensor). An entry the worker points a kind at
with constant `SetTriggerEntry(…, kind, entry)` gets that kind's name with its number
(`OnTalk_6`: the opened treasure chest). A worker that sets its table with a computed
kind or entry gets none.

**What the code does** (natives by their reference-assembly name, inline methods by
their code, waiting not counted; the first rule that fits):

| Name | The method's code |
|---|---|
| `Say12`, `Say12To15`, `Say12Etc` | only message windows and voices (`Msg*`, `Voice*`, the speaker's `AutoTalk`), constant texts: the one text, the first and last of several in order, the first of several |
| `PlayVoice` | only voices, no text |
| `MoveCamera`, `PlayMotion`, `Walk`, `TurnInPlace`, `Place`, `PlaySound`, `RunEffects`, `SetLights`, `DrawSprites`, `FadeScreen` | only natives of that one kind |
| `Walk`, `TurnInPlace`, `PlayMotion`, `Place` | only the worker's own body (move, turn, motion, position, shown or hidden), by the first of these it uses |
| `RunEffects` | effects (`Fx.*`) and their sounds |
| `Unload` | detaching or disposing the model (`WorkerDetach`, `WorkerDetachAndDispose`, `Chr.DisposeData*`), also hidden and stopped first |
| `Hide`, `Show`, `BodyHitOn`, `BodyHitOff`, `DisableTriggers`, `EnableTriggers` | only visibility, collision and trigger switches (and effects), by the one `Chr.Hide` value, else the one kind |
| `GoTo<Map>` | every map change to one constant map (`GoToKami0100`) |
| `Scene12` | texts among other code: the first text shown |
| the native's name | one native, any number of times (`SetDistanceCull`, `TriggerSetActive`) |
| `MoveCamera`, `Walk`, … | at least six calls, two thirds of them of one kind |

Requests to other workers, `call`s and stores to variables rule out the "only" rows. A
name several methods of a worker get takes each one's entry (`Hide_7`, `Hide_9`); a name
the worker, the script or the reference assembly has is not taken. Empty methods that are
no trigger entry keep their number: nothing in them says what they are for.

## Workers

Added 2026-11-10 to the role rules (`WorkerNaming`):

- **Party members**: an actor (type 1) that loads several models, or none by a constant, but binds
  itself to one party member with constant `SetPartyIndex(k)` (native `0x0042`: worker `+0x40` =
  k, `g 0x0132ffa0[k]` = the worker; "0 Tidus, 1 Yuna, …", `pc.ath`'s order, `Target` 0-6) is
  that character: `Tidus` (`Tidu`, then `TiduH`), `Wakka`, … 414 workers.
- **`BattleLine`, `BattleBox`**: a trigger that starts a battle (`Btl.LabelExe`, 0x7002,
  `MsBattleLabelExe(a, 1, b)`).
- **`WarpLine`, `WarpBox`**: a trigger that jumps to an entrance of the same map
  (`Fx.MapJumpToEntrance`, 0x8006, `Atel_JumpToEntrance`) and changes no map.
- **`MapSetup`**: a logic worker that calls a map-display native (`MinimapSetCorner` and the other
  `MinimapSet*`, `SetMapMarkerPos`: the world map marker, `Fx.GuideMapSetLimit`,
  `FootstepHookEnable`) and otherwise only `Screen.*` (lights, fades), music, the player's control
  and waiting; no requests, no calls. (Without that limit the rule took 409 workers, blitzball's
  8,000-line logic among them; with it 99.)
- `DialogueWorker`, `DialogueLine`, `DialogueBox` also allow the speaker's mouth
  (`Chr.SetAutoTalkOf`, `Chr.SetAutoTalk`), as the scripts set it around every spoken line.

## Registers

`RegisterRoles`: a worker's register (`I0`-`I3`, `F0`-`F9`; the compiler places it by
`[Register(n)]`) every store to which, in all its methods, is the result of one native getter
(`Get` in its name, no address or offset in it), and which is read, is named after it without
`Get`: `TurnKind` (`Common.GetTurnKind`, 350), `ActorMotionInfo`, `WorkerId`, `SelfProperty`,
`ComInfo`, `ParsedCommand`. A register stored from a constant, a computation or a loop keeps its
number: 8,765 of 9,181 do. A name the worker, the script or the reference assembly has, or one two
registers of the worker would take, is not given.

## Long entries

Entries of at least six native calls, three quarters of them of one kind (`Walk`, `MoveCamera`,
…), take that kind's name also when they request or call other workers (the rule above without
requests allows two thirds).

## Function workers

`WorkerNaming.FunctionRole`, from the natives the function worker calls (on its caller's
work, as `call` runs it there): `SetMoveTalkDefaults` (only setters of the caller's move,
turn, talk and touch parameters, under a `GetOwnWorkerType()` switch: the 392 users of
the library class `MoveTalkDefaults`), `AttachEquipment` (attaches models by
`ChrAttachModel(slot, ChrGetEquipModel(kind), bone)` to the caller's party slot),
`DetachEquipment` (only `ChrDetachModel`).
