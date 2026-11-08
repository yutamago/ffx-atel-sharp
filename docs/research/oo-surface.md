<!-- SPDX-License-Identifier: MIT -->

# An object-oriented surface over the natives

The decision of 2026-11-04 ([plan.md](../plan.md), "Decisions (user)"): the source reads object-oriented.
Natives that act on the worker become its members, get/set pairs properties, natives on another worker members
of that worker, and the bytecode stays identical (Phase 1). This note is how it was built and measured; the
reference is [language.md](../language.md#members).

## What the scripts call

3,146,543 native calls in the 1,621 shipped scripts (`data/natives.json`, `calls`), 1,059 natives called.
The calls are concentrated: the 20 most called natives are 60 % of all calls, the 100 most called 90 %.
The most called fall in a few families:

| Family | Natives (calls) |
|---|---|
| sprites, by slot | `SpriteShow` 410,172, `SpriteHide` 139,694, `SpriteSetColor` 129,465, `SpriteSetScale` 108,201, `SpriteSetPos` 48,683 (35 scripts) |
| the camera | `SetScreenDepth` 136,808, `SetRoll` 135,261, `FocusOrbitScaled` 108,135, `EyeOrbit` 103,182, `FocusTimeEase` 71,471, `MoveEased` 70,375, `Wait` 41,536, `FocusWait` 39,331 |
| waiting | `WaitFrames` 124,718, `WaitForever` 14,728, `WaitFramesUnlessHddMode` 8,659 |
| message windows | `MsgSetPos`/`SetStyle`/`Open`/`SetFlags`/`SetText` 33,038 each: almost all inside `Dialogue.*` calls already |
| the model's motion | `SetMotion` 38,040, `SetMotionBlend` 30,659, `SetNextMotionBlend` 30,539, `SetAutoMotion` 23,505, `WaitMotion` 12,465, `PlayMotion` 11,439, `ReleaseMotion` 6,376 |
| the worker's turn and move | `TurnSetYaw` 20,727, `TurnStart` 20,623, `MoveWait` 16,314, `MoveSetTarget` 16,109, `MoveStart` 14,533, `WorkerStopMove` 14,378, `SetYaw` 14,294, `GetYawToMoveTarget` 13,643, … |
| another worker | `GetWorkerId` 27,417 (−1 in 27,060), `SetAutoTalkOf` 16,838, `GetWorkerX/Y/Z` ~6,900 each |

So the surface started from those, in that order, with groupings the natives' own names and documentation
give: `TurnSetYaw`, `TurnStart`, `TurnWait`, `GetTurnKind` are the turn's; `WorkerStopTurn` stops it. A name
comes from the native's verified name or its documented meaning, never from a guess ("Only verified names").

## The design

- **Data-driven.** `data/native-members.json` says, per native, which member it is: a path (`Pos`,
  `Turn.Start`, `Pos.X`, `Sprites.Show`, `Wait.Frames`), whether it is a getter or a setter, whether its first
  argument is a worker, whether a setter's value is a `Vector3` (three floats) or a `bool`. `tools/gen-members.cs`
  writes `src/Ffx.Atel.Game/Members.g.cs` from it and `data/natives.json` (no game files needed), with each
  member's doc comment from its native's. Each member carries `[NativeMember(id)]` on the method or accessor.
- **One table for both directions.** `Ffx.Atel.Compiler.NativeMembers` reads those attributes from the
  reference assembly: by path for the compiler (`StatementReader.Members`), by native for the decompiler
  (`StatementPrinter.Members`). Adding a native to the JSON and regenerating is all a new member needs.
- **Where members live.** A member of the worker is a `protected static` member of `Worker`: requested
  entries are static (`public static async Task E8Async()`), and there is one instance of each worker. A
  component (`Turn`, `Move`, `Motion`, `Neck`) is a property of `Worker` whose class holds the members; an
  indexed group (`Sprites`) a property with an indexer; an engine-wide group (`Wait`, `Camera`, `Sound`) a
  static class.
- **Another worker.** A native whose first argument is a worker is reached unqualified (this worker:
  `Common.GetWorkerId(-1)`, the idiom of 27,060 calls), through the worker's class (`Tidus.Pos.X`: Tidus's
  number, a constant) or through `Workers[n]`. C# reads `Tidus.Pos` as the static member through the derived
  class; the compiler, which reads the syntax, takes the class as the worker's number. Where the game has a
  separate native for this worker (`GetYaw` and `GetWorkerYaw(w)`), the member alone is that one, and the
  worker native with `GetWorkerId(-1)` stays a native call (it would read back as the other).
- **A worker's number is its position in `[Workers]`.** `Common.GetWorkerId(-1)` returns the worker's
  `+0x2e`, the id the worker natives take (`Script_GetWorker`), and requests address workers by the same
  numbers. In the corpus, the constant workers that `SetAutoTalkOf` and `SetNeckTarget` name (16,272 calls)
  are, by that numbering, a worker that loads a model in 13,238 calls; the other 3,034 name workers whose code
  is a library class (whose model call is in the library). So a constant worker argument is written as the
  worker's class.
- **`Vector3`** is `Ffx.Vector3`, three read-only floats, not `System.Numerics.Vector3`: ATEL has no structs,
  so a vector is never stored or computed with; it is only the value of a property (`new Vector3(x, y, z)`,
  whose values go to the native as written) and read by component. A .NET type would offer arithmetic that
  cannot compile, and a `using` the files do not have. The analyzer allows `new Vector3(…)` only as a
  property's value (ATL1001 elsewhere).
- **`Motion = id`.** The user's example assigns the motion. `Motion` is a component (`Motion.Play`,
  `Motion.Wait`) and takes an assignment through an implicit conversion from `int`, which the compiler lowers
  to `Chr.SetMotion(id)`. Reading the current motion is `Motion.Current` (`Chr.GetMotion`), a separate native.
- **Methods for several values.** `Common.TurnSetFrames(elapsed, frames)` is `Turn.SetFrames(0f, …)`, not a
  tuple property (`Turn.Frames = (0f, …)`): a method is plainer C# and needs nothing new in the compiler.
- **`Move.Stop()`** for `WorkerStopMove`, rather than `StopMove()`: it sits with `Move.Start`/`Move.Wait`,
  as `Turn.Stop()` (`WorkerStopTurn`) sits with the turn's.
- Natives whose meaning is unverified keep their names and their form (`Common.CurLevelSet5ec(8)`).

## Numbers (2026-11-04)

- 88 natives are members (65.4 % of the 3,146,543 calls).
- `atel roundtrip --check`: 1,621 / 1,621 scripts byte for byte, 0 C# errors, 221 warnings (ATL2006, as
  before). The decompiler wrote 2,035,998 member uses: 26,256 of them a worker native on this worker
  (`GetWorkerId(-1)`), 37,770 on another worker (`Tidus.…`, `Workers[n].…`).
- Native calls written as `Group.Native(…)` in the decompiled source: 2,845,854 before, 792,135 after.
- Library classes: workers whose body names another worker by class are no longer shared between scripts
  (the name means another worker in each); 2,998 workers derive from 430 classes (about 3,006 before).

The remaining calls are mostly: the camera's orbit natives (`FocusOrbitScaled`, `EyeOrbit`, 211,317, whose
first arguments are not described yet), message windows outside dialogue lines (`MsgSetValue` 45,839), the
battle natives (already typed with enums: `Btl.GetActorProperty(Target.Own, ActorProperty.Hp)`), `Random16`
and `Random`. They are the next candidates, once their meaning is verified.

## Numbers (2026-11-08): nested groups

- Static groups may nest (`Camera.Eye`, `Camera.Focus`, `Camera.Screen`), and `Voice` is a group: 168
  natives are members (74.5 % of the 3,146,543 calls; 91 and 65.4 % before). `Camera` holds 8 natives
  (275,290 calls), `Camera.Eye` 37 (268,416), `Camera.Focus` 31 (271,657), `Camera.Screen` 8 (274),
  `Voice` 4 (30,059).
- `atel roundtrip --check`: 1,621 / 1,621 byte for byte, 0 C# errors, 221 warnings (ATL2006, as before). The
  decompiler wrote 1,287,033 member uses (16,078 on this worker, 35,367 on another).
- 19 camera natives with calls stay `Cam.…` (1,514 calls): the variable readers (`GetPos`, `RefGetPos`,
  `GetEyeVars`, `GetRefVars`), `BtlRandom`, and natives whose meaning or side is not documented yet.

Next candidates: the shake kinds as one `Shake(kind, …)` and `Btl`'s actor properties as properties. Both
pass the choice as an argument (each kind is its own native, the property is the native's argument), so the
compiler would need to pick the native or the argument from a constant first.

## Repeated sequences

Repeated statement sequences become `[Inline]` helpers (the second 2026-11-04 decision), written with these
members: an inline method of a worker, or of `Worker` in the reference assembly (`src/Ffx.Atel.Game/Inline/`,
called without a class name), may say `Turn.Yaw = yaw; Turn.Start(0, 4099, 0); Turn.Wait();` and expands to
the natives' calls (`MemberTests.InlineMethodsMayUseMembers`).
