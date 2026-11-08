<!-- SPDX-License-Identifier: MIT -->

# The Atel language

Atel is standard C#, restricted to what FFX's script machine (ATEL) can run. Files are `.cs`, the
compiler is Roslyn plus our own backend, and the IDE is any C# IDE. What makes C# Atel is:

- the **reference assembly** `Ffx.Atel.Game` (`src/Ffx.Atel.Game`): the game's natives, enums and
  constants, the worker base classes and the attributes that describe a script;
- the **analyzers** ([analyzers.md](analyzers.md)): errors for C# that ATEL cannot run;
- the **compiler** (`src/Ffx.Atel.Compiler`), which accepts a narrower set of statements and
  expressions than the analyzers allow. Where it does not compile something yet, this page says so.

This page describes what is on `main`. [plan.md](plan.md) has the design and the decisions behind
it, including parts not built yet; how each construct becomes bytecode is in
[encoding.md](encoding.md); a first mod is in [tutorial.md](tutorial.md).

Status marks used below: **available** (the compiler compiles it), **decompiler only** (the
decompiler writes it so vanilla source compiles back to the same bytes; new code rarely needs it),
**planned** (in the plan, not built).

## The machine in brief

An ATEL script is one block of bytecode in an event (`.ebp`), monster or battle file. It has
**workers**, each with numbered **entries** (code offsets). The engine runs entries 0 and 1 of each
worker; workers ask each other to run entries with **requests**. Each worker runs one request at a
time per **priority level**; a higher level interrupts a lower one. Code is a stack machine with:

- **variables** in storage areas (the save game, common variables, the worker's private data, the
  script's shared data, the event data), typed `u8` … `f32`, scalar or array;
- per worker 4 integer registers (`i0`-`i3`), 10 float registers (`f0`-`f9`), registers A and B and
  a float accumulator;
- **natives**: about 1,500 game functions by number (`func 0x0000`), which may wait (poll) for
  frames;
- integer and float **constant pools** per script.

There are no strings, objects, pointers (beyond the address of a variable), exceptions or
short-circuit operators. The stack is empty at every jump and label, so a statement is an
expression tree between jumps ([corpus.md](corpus.md#stack)). What new code needs beyond that
(`&&`, `?:`, locals) the compiler builds from jumps, registers and hidden variables ("New code"
below).

## Scripts

A script is a folder and a namespace. `atel decompile <script> -o <dir>` writes
`<dir>/Monsters/M209/`, `<dir>/Events/Bj_BaajTemple/Bjyt0400/`, `<dir>/Battles/Kino_MushroomRock/Kino00_00/`
(the map code with the area's English name where `data/areas.json` has one):

| File | Holds |
|---|---|
| `M209.cs` | the **script class**: the file it is, its workers in order, its variables |
| `M209.Layout.cs` | what only pins the compiled layout: on a part of the script class `[Header]`, `[Pin]`, `[Patch]` and the pools, on a part of each worker that has one its `[Worker(…)]` |
| `M209.Data.cs` | the data areas as hex: on a part of the script class `[SharedData(…)]` and the others, on a part of a worker its `[PrivateData(…)]` (no file when the script has none) |
| `W01.cs`, `RikkA.cs`, … | one file per worker; a big worker is `partial` over `W36.cs`, `W36.2.cs`, … |

The script class is `public static partial class`, with one of `[Event("bjyt0400")]`,
`[Monster("m209")]` or `[Battle("kino00_00")]` and the worker order:

```csharp
[Monster("m999")]
[Workers(typeof(W00), typeof(Larva), typeof(W02))]
public static partial class M999
{
    [Var(Storage.Shared, 0x0000)] public static int Turns;
}
```

A worker's number is its position in `[Workers]`. Nothing else about the order of files or
classes matters, except the order of a worker's methods (see "Layout").

Every file starts with `using Ffx; using Ffx.Atel; using static <Namespace>.<ScriptClass>;` so that
natives, attributes and the script's variables need no prefix.

## Workers

A worker is a class deriving from its worker type (worker header `+0x00`). Each type has a name
after what FFX.exe does with it; the numbered classes `Worker0` … `Worker6` are the same types and
still compile (the named ones derive from them):

| Base | Type | What FFX.exe does with it | Where |
|---|---|---|---|
| `FunctionWorker` | 0 | the target of `call`, runs on the caller's worker | events |
| `Actor` | 1 | steps it each frame as a character: move, turn, the model it is bound to, then talk and touch against the player; the only type that places a model and binds effects | events |
| `LineTrigger` | 2 | checks its line (`TriggerSetLine`) against the player each frame: talk, cross, touch | events |
| `BattleWorker` | 2 | every monster and battle worker (the same type as `LineTrigger`) | monsters, battles |
| `BoxTrigger` | 3 | checks a box around its position against the player each frame: talk, cross, touch | events |
| `LogicWorker` | 4 | no position block, no frame step, no triggers: only its entries | events |
| `LineSensor` | 5 | checks its line against another worker's movement (its target, tracked workers); two levels | events |
| `BoxSensor` | 6 | checks its box against its target worker or the player; two levels | events |

From FFX.exe: `Atel_WorkerFrame_008666e0` dispatches each frame on the type byte (1: move and turn
updates, then `Atel_CheckTouchTalk_00866980`; 2: `Atel_CheckLineTouch_008684b0`; 3:
`Atel_CheckBoxTrigger_00866cc0`; 5: `Atel_CheckLineCrosses_00868020`; 6: `Atel_CheckBoxTouch_00867c10`;
4 and 0 nothing); `AtelGetPosWorkAdrs` gives type 4 no position block and types 5 and 6 a smaller
work with two levels (`+0x284`, not `+0x558`); only type 1 places a character
(`Atel_WorkerPlaceChr_00866800`) and binds effects (`AtelBindFp`). The trigger entries each kind of
trigger runs by default, by type (ffx-rust `docs/formats/script.md`, "Trigger entries"):

| Type | Talk | Touch (each frame) | Cross | Touch begins | Touch ends | Level |
|---|---|---|---|---|---|---|
| `Actor` | 2 | 5 | - | - | - | 7 (talk), 5 (touch) |
| `LineTrigger`, `BoxTrigger` | 2 | 5 | 4 | 6 | 7 | 7, 3, 5, 4, 2 |
| `LineSensor` | - | - | 2 | - | - | 1 |
| `BoxSensor` | - | - | 2, then 3 | 2 | 3 | 1 |
| the others | - | - | - | - | - | none |

`SetTriggerEntry` changes a worker's entries (an opened treasure chest points talking at another one).

```csharp
[Worker(PrivateLength = 0x18)]
public sealed partial class Larva : BattleWorker
{
    [Register(0)] public static int I0;

    protected override void Init() { … }
    protected override void Main() { … }

    [Entry(2)]
    void E2() { … }
}
```

- The decompiler names a worker after the model it loads (`RikkA`, `TreasureChest_14`), in monster
  and battle scripts after the battle's handler it serves (`Ai`, `Cameras`, `TidusActions`, with
  entries `OnTurn`, `OnHit`, `OnDeath`; [research/handlers.md](research/handlers.md)), else after what
  its code does (`Director`, `ExitToKami0100`, `CameraLine_21`; [source-repo.md](source-repo.md#names)),
  else `W03`.
- `[Worker(Word0C = …, PrivateLength = …)]` carries two header words; `[PrivateData(length, hex)]`
  the worker's private data block. **Decompiler only.**
- A worker holds only entry methods and `[Register]` fields. Other members are compile errors.
- **Library classes**: workers that are identical across scripts derive from a shared class in
  `Library/` (`public sealed partial class W03 : Shared0001 { }`); the compiler lays the library
  class's members out before the worker's own. `atel compile` finds the nearest `Library/` above
  the script folder, or takes `--library <dir>`.

## Entries

An entry method's attributes or signature say which entries start at it:

| Declaration | Entries | Notes |
|---|---|---|
| `protected override void Init()` | 0 | the engine runs it when the worker starts, at level 7 |
| `protected override void Main()` | 1 | the worker's main entry, level 0 |
| `[Entry(5)] void E5()` | 5 | an entry nothing in the script requests by name |
| `[Entry(5)] public static async Task E5Async()` | 5 | an entry other workers request (see "Requests") |
| `[Entry(2)][Entry(3)] void E2()` | 2 and 3 | entries that start at the same code |
| `public static void Run()` in a `FunctionWorker` | 0 | a function worker's one entry |

**Available.** Notes:

- `async void` is allowed on an entry the engine runs (`protected override async void Main()`,
  `[Entry(4)] async void E4()`) so that it can `await` requests; nothing waits on its result
  ([ATL2007](analyzers.md#atl2007)).
- Requested entries are `static`: C# reads `Lulu.E5Async()` as a member of the class `Lulu`. The
  worker's registers are `static` with them. There is one instance of each worker class, so this
  changes nothing.
- A method named `Init` without `override` hides the base's (CS0114) and is not entry 0.
- `Entry(n);` inside a method marks where entry `n` starts in the middle of it (no code). The
  decompiler writes it where one entry's code jumps into another's: C# has no `goto` between
  methods, so those entries' methods become one. **Decompiler only.**
- `return;` ends the entry (`end`; `ret` in a function worker). `End(value)` and
  `End(value, reset: true)` end it with a value (`endp`, `endp_1`); `Halt();` is `op_54`.
- Entry numbers are explicit in vanilla source. In a mod, new entries get their numbers when the
  loader links ("Mods" below).
- A requestable entry may take parameters and return a value ("Parameters and results" under
  "Requests").

## Function workers and helpers

A function worker (`FunctionWorker`, `Worker0`) runs on its caller's worker, with the caller's registers, as a
subroutine (`call`). Its one entry is `public static void Run()`; the call is `W48.Run();`
(`Call<W48>();` reads the same). A function worker may use `return;` (`ret`). Calls nest at most 3
deep and must not recurse ([ATL3001](analyzers.md#atl3001), [ATL3002](analyzers.md#atl3002)).

Function workers stay classes deriving from `FunctionWorker`, not `static class`es as the plan first said:
a static class cannot derive, and the type, the `Worker` helpers and library classes all need the
base class.

### Helper methods

**Available.** A plain method (no attribute, not `async`, not an `override`, not a function worker's
`Run`) of a worker or of the script class is a helper: the compiler makes it a hidden function worker
and runs it with `call`, synchronously, on the caller's worker.

```csharp
public sealed partial class Larva
{
    [Postfix(nameof(Init))]
    void Tougher() { Btl.SetActorProperty(ActorProperty.Hp, Scaled(1234, 3)); }

    static int Scaled(int hp, int percent) => hp + hp * percent / 100;

    void Count() { Turns++; }
}

public static partial class M209
{
    public static int Turns;
    public static int Twice(int x) { return x * 2; }   // callable as Twice(…) from every worker
}
```

- Called as `Scaled(…)` from its own class, `Larva.Scaled(…)` from elsewhere, and a script class
  helper as `Twice(…)` from any file (the `using static` line) or `M209.Twice(…)`. Static or not
  makes no difference; there is one instance of each worker.
- **Parameters and the result** (`int`, `float`, `bool`, enums) are locals of the helper: the caller
  evaluates every argument, writes the parameters, calls, and copies the result. They live in
  registers the callers' workers leave free, else in hidden variables, like any local (plan,
  "Parameters and return values"). Named arguments work; `ref`, `out`, `params` and default values do
  not (yet).
- A block body or an expression body (`=> x * 2`); `return value;` anywhere.
- A helper may call natives, request entries, wait, and call other helpers and function workers. A
  helper that waits makes its callers keep their locals across the call in hidden storage.
- At most 3 calls deep counting from an entry, and no recursion: compile errors, as
  [ATL3001](analyzers.md#atl3001) and [ATL3002](analyzers.md#atl3002) report in the IDE.
- Its code runs with the caller's registers, so a helper of a worker may use that worker's
  `[Register]` fields.

Any other method call (not a native, a request, a helper, an inline method, `Run()` or a `Worker`
member) is a compile error ("no ATEL call").

### Inline methods

**Available.** A method with `[Inline]` is not called: the compiler writes its code in place of each
call, with every parameter replaced by the argument given for it, like a C macro. The code is what the
statements would give written out at the call site, labels and constants included
([encoding.md](encoding.md#inline-methods)), so the game's own code can be written with them and still
compile to the shipped bytes. The decompiler writes a call wherever the code is exactly an inline
method of the reference assembly's, and folds the code a worker repeats into inline methods of the
worker's own ([Repeated code](#repeated-code)).

The reference assembly's are the lines of dialogue, `Ffx.Dialogue` (the message natives, whose meaning
is known from FFX.exe):

| Call | Stands for |
|---|---|
| `Dialogue.Open(text, …)` | `MsgSetPos`, `MsgSetStyle`, `MsgSetText`, `MsgSetFlags`, `MsgOpen`: the window opens, nothing waits |
| `Dialogue.WaitUntilOpen(window)` | `while (MsgGetState(window) != 2) WaitFrames(1);` |
| `Dialogue.WaitUntilReady(window)` | `while (true) { if (MsgIsReady(window) == 1) break; else WaitFrames(1); }` |
| `Dialogue.Say(text, …)` | `Open`, `WaitUntilReady`, `MsgWaitEnd` (the player reads and confirms), `MsgClose`, `MsgWaitExEnd` |
| `Dialogue.SayVoiced(text, …)` | `Open`, `WaitUntilOpen`, `VoiceStartEnd`, a loop that stops the voice on a button press once the window is ready, `SoundWaitPoll`, `MsgClose`, `MsgWaitExEnd` |
| `Dialogue.SayVoicedToEnd(text, …)` | as `SayVoiced` without the button: the voice plays to its end |
| `Dialogue.SayWithVoice(voice, text, …)` | `VoiceStandbyEnd(voice)`, `WaitFramesUnlessHddMode(5)`, then `SayVoiced`: a voiced line with its voice prepared |

Each takes `text`, then `window = 0`, `x = 256`, `y = 448`, `anchor = 4`, `style = 1`, `flags` (12290,
or 8194 for the voiced ones) and `mode = 0`: the values most lines use, so a usual line is one short
call. A voiced line is prepared first, as the game does:

```csharp
Chr.SetAutoTalkOf(2, 1);                // the speaker's mouth moves (worker 2)
Common.VoiceStandbyEnd(0x85355cb);      // prepare the voice
Common.WaitFramesUnlessHddMode(5);
Dialogue.SayVoiced(Text[0]);
Chr.SetAutoTalkOf(2, 0);
Dialogue.Say(Text[71]);                 // a line without voice
Dialogue.Say(Text[19], window: 1, y: 224, style: 0, flags: 0);
```

The other helpers of the reference assembly are written with the object-oriented members
([Members](#members)): a component's on the worker's own component (`Turn.To(…)`, never
`Tidus.Turn.To(…)`), `Worker`'s without a class name. Each name says what its natives do:

| Call | Stands for |
|---|---|
| `Turn.To(yaw, small = 15, large = 30, mode = 4099)` | `TurnSetYaw(yaw)`, `I3 = GetTurnKind()`, the turn's motion (`GetTurnMotion(I3)`) and frames (`GetTurnFrames(I3, small, large)`), `TurnStart(0, mode, 0)`, `TurnWait()`, `ReleaseMotion()`; `yaw` is `[Eager]` (`Turn.To(Move.YawToTarget)`), and the call overwrites `I3` |
| `Turn.InFrames(frames, yaw, mode = 4099)` | `TurnSetFrames(0, frames)`, `TurnSetYaw(yaw)`, `TurnStart(0, mode, 0)`, `TurnWait()` |
| `Move.To(x, y, z, turnSpeed, turnSpeed2 = 0.17453294f, turnMode = 53250, moveMode = 49153)` | the turn's speeds, `MoveSetTarget(x, y, z)`, `TurnSetYaw(GetYawToMoveTarget())`, `TurnStart`, `MoveStart`, `MoveWait()`, `WorkerStopTurn` of this worker |
| `Move.StartWithTurnAndWait(turnMode, moveMode)` | `TurnStart`, `MoveStart`, `MoveWait()`, `WorkerStopTurn` of this worker |
| `Move.InFrames(frames, mode)` | `MoveSetFrames(0, frames)`, `MoveStart(0, mode, 0)`, `MoveWait()` |
| `ChangeMap(map, entrance, fadeFrames = 15, fadeType = 0)` | `SetMapFadeFrames`, `SetMapFadeType`, `MapChange(map, entrance)` |
| `JumpToMap(map, entrance, fadeFrames, fadeType = 0)` | the same with `MapJump` |
| `Party.RestoreAll()` | `CharRestoreHp` and `CharRestoreMp` of characters 0-6 and 8-17, in the save spheres' order |

The user's Besaid example, `bsvr0000`'s `Rikku.E8Async`:

```csharp
Wait.Frames(95);                                       Wait.Frames(95);
Turn.Yaw = 1.572062f;                                  Turn.To(1.572062f);
I3 = Turn.GetKind();                                   Motion.Play(0x71050, 0, 1, 0, -1);
Motion = Turn.GetMotion(I3);
Turn.SetFrames(0f, Turn.GetFrames(I3, 15, 30));
Turn.Start(0, 4099, 0);
Turn.Wait();
Motion.Release();
Motion.Play(0x71050, 0, 1, 0, -1);
```

A new line in a mod is a new message and a call (`Text.New` needs a script with message tables, an
event or a battle):

```csharp
public sealed partial class Wakk
{
    [Postfix(nameof(E5Async))]
    void AfterTheLine()
    {
        Dialogue.Say(Text.New("Nice throw!"));
    }
}
```

- An argument is substituted where its parameter is used and evaluated there, as often as the
  parameter is used. So it is a constant, a variable, `Text[n]`, `Text.New("…")` or an expression of
  them (`V104[V191 + 1] + 20`); a call, `x++`, `?:` or `&&` in an argument is an error
  ([ATL3004](analyzers.md#atl3004)). Compute such a value into a variable first. The exception is an
  `[Eager]` parameter (below).
- Arguments go by position or by name; a parameter with a default may be left out.
- Your own: `[Inline]` on a method of a worker or of the script class. It returns `void`, calls
  natives, function workers and other inline methods with `if`, loops and `switch` around them,
  requests entries, and may read and assign the script's variables (`V145 = 1;`, `Table[i] += 2;`);
  one of a worker also that worker's `[Register]` fields. One that awaits a request
  (`await W18.E9Async();`) is `async Task`, and a call of it is `await Name(…);`. Mark it
  `[Inline(Waits = true)]` when it calls a native that waits, requests, calls a function worker or
  holds an opcode statement (the compiler checks). It cannot call itself, directly or through others
  ([ATL3003](analyzers.md#atl3003)), jump or return. A call is no `call`: it adds no level to the 3
  that `call` allows. A worker's inline methods are found through the library class it derives
  from, too.
- **A local in a register.** An inline method's local lives in a register of the calling worker that
  the method names, as the game's code keeps it there: `[InlineLocal("kind", 3)]` and
  `int kind = Turn.GetKind();` lower to `I3 = Common.GetTurnKind();`. The call overwrites that register
  (the compiler then keeps the worker's own locals out of it). Any other local in an inline method is
  an error.
- **`[Eager]` parameters.** A parameter used once, in the method's first statement, with no other
  native called there before it, may be marked `[Eager]`: substituted, its argument is evaluated once,
  first, as in an ordinary call, so it may be a native's call:

  ```csharp
  [Inline(Waits = true)]
  [InlineLocal("kind", 3)]
  public void To([Eager] float yaw, int small = 15, int large = 30) {
      Common.TurnSetYaw(yaw);
      int kind = Common.GetTurnKind();
      …
  }
  Turn.To(Common.AngleToPoint(Tidus.Pos.X, Tidus.Pos.Z));   // the call runs once, where TurnSetYaw takes it
  ```

  The compiler checks the claim.
- In the vanilla source, lines inside dead `if (false)` blocks stay written out, as the original
  compiler left them.

### Repeated code

The decompiler writes code that a worker repeats with only different values once, as an inline
method of the worker, and a call at every place (docs/plan.md, 2026-11-04;
[research/repeats.md](research/repeats.md)):

```csharp
    Repeated01(54528);                                  // Kami0200, TidusH: four places
    Repeated01(0x1af00);
    …
    /// <summary>Code this worker repeats with only different values, written once: 4 calls (…).</summary>
    [Inline(Waits = true)]
    private static void Repeated01(int value)
    {
        V1 = Motion.Frame;
        do
        {
            Wait.Frames(1);
            if (V1 >= Motion.Frame)
            {
                break;
            }
        } while (Motion.Frame < value);
    }
```

- **Found per worker**, after the reference assembly's inline methods are folded: the miner of `atel
  repeats` on the worker's statements (runs of whole statements of one block that recur with the same
  shape), its greedy cover taking the run that saves the most lines first. A run of at least 8 lines,
  saving at least 100, with at most 6 parameters becomes a method; a parameter is a constant that
  differs between the places, everything else stays in the body. A body may call methods taken after
  it (a small helper found inside a large one).
- **Checked.** Each method gives its first place back; the worker is lowered again with the calls in
  place and compared with the original instruction by instruction, and keeps nothing if it differs.
  Dead `if (false)` blocks and `Cut.*` names stay in the bodies as they were; code inside a dead block is
  never folded.
- **Where.** In the worker's class, after its entries. Its code uses the worker's registers and the
  script's variables by their numbers (`V145` is another variable in another script), so it cannot be
  a library helper shared between scripts; in the worker's class it is next to the code that calls it,
  a mod's `partial` class of the worker can call it too, and when the library shares the worker
  between scripts the methods go with the worker into its library class.
- **Names.** `RepeatedNN`, numbered in the order the cover takes them, unless
  `data/inline-names.toml` names the body's signature (a hash of its statements as lifted from the
  bytecode: natives by number, constants by value, variables by location; not of its C#, so a name
  stays when the decompiler's C# changes):
  `WinPoseCamera`, `ShowStatIcon`, `ScoreGoal`, `DrawPlayerPanel`, `ScrollList`, `SayWithVoiceSkippable`,
  `WaitForMotionFrame`, …. The names say what the natives and members do (for the blitzball HUD, read
  with the texts it shows); the same code in several workers or scripts has one name. The table may
  name the parameters (`ShowStatIcon(stat, slot, highlight)`) and give a summary, written as the
  method's `<summary>` (the calls go to `<remarks>`). A parameter it does not name is named after the
  native argument it is first passed to (`motion`, `slot`, `x`), typed as that argument (`TextId`, an
  enum) when every use is one. When a worker has two methods of one name, the second is `Name2`.
- `atel decompile` writes them by default; the API's `RepeatOptions.Disabled` turns them off.

## Requests

A request asks a worker to run one of its entries at a priority level. Requests are written as
calls of the entry's method:

| C# | Opcode | Does |
|---|---|---|
| `_ = Lulu.E5Async();` | `req0` | queues entry 5 of `Lulu` and carries on |
| `await Lulu.E5Async();` | `reqe0` | waits until it has ended |
| `await Lulu.E5Async().Finished;` | `reqe0` | the same |
| `await Lulu.E5Async().Started;` | `reqw0` | waits until it has begun |
| `_ = Lulu.E5Async(Priority.P3);` | `req0` at level 3 | the level; level 1 without it |

**Available.**

- The entry must be declared `async Task` (`Ffx.Atel.Task`, not .NET's) and have an entry number
  (`[Entry(n)]`, `Init`/`Main`). Requesting a method that is not `async Task` is an error
  ([ATL2002](analyzers.md#atl2002)); a request that is neither awaited nor discarded gets a hint
  ([ATL2001](analyzers.md#atl2001)).
- To pass a priority, the entry declares the parameter:
  `public static async Task E7Async(Priority priority = Priority.P1)`. It is the request's level, not
  a value the entry sees.
- `Priority` is `P0_Main`, `P1` … `P6`, `P7_Init`, `P8`. Scripts use 1-6; 0, 7 and 8 are the
  engine's levels and give a warning ([ATL2006](analyzers.md#atl2006)).
- `await` only takes a request. Awaiting a native is a C# error (natives return `int`, `bool` or
  `float`); a native that waits does so by itself.
- Requests within one worker: awaiting a worker's own entry only works when the request's level is
  above the level the caller runs at, else the caller waits for ever
  ([ATL2004](analyzers.md#atl2004), [ATL2005](analyzers.md#atl2005)).
- **The fallback form** for requests the call form cannot express, written by the decompiler:
  `Request(level, worker, entry)`, `RequestWaitStart(…)`, `RequestWaitEnd(…)` (`req0`, `reqw0`,
  `reqe0`; `variant: n` for `req1` … `req5`), and `RequestParty(level, slot, entry)` (`op_39`, a
  request to a party member's worker). It is used for a computed worker or entry, requests to the
  worker's own entries, entries without a method of their own, and the variants. In a mod,
  `Link.Worker<T>()` and `Link.Entry<T>(nameof(…))` give the numbers for it.

### Parameters and results

**Available** (new code). An entry other workers request may take parameters and return a value
(plan, "Parameters and return values"):

```csharp
public sealed partial class Larva
{
    public static async Task<int> DamageAsync(int amount, Priority priority = Priority.P1)
    {
        Btl.SetActorProperty(ActorProperty.Hp, Btl.GetActorProperty(Target.Own, ActorProperty.Hp) - amount);
        return Btl.GetActorProperty(Target.Own, ActorProperty.Hp);
    }
}

// in another worker's entry:
int left = await Larva.DamageAsync(100);
_ = Larva.DamageAsync(amount: 5, priority: Priority.P3);   // queued; the result is not read
```

- ATEL's requests carry only level, worker and entry. Each parameter and the result are **hidden
  fields of the receiving worker**, one per parameter and entry: in the worker's private data when
  only the worker itself requests the entry, else in the script's shared data (which two monsters of
  one kind share). The
  request writes the arguments, then requests; the entry reads its parameters as locals (they live
  in their fields, so they survive waits); `return v;` writes the result field before `end`, and
  `await` reads it after the entry has finished.
- A value needs `await …` or `await ….Finished`. `.Started` and `_ =` do not wait for the end, so
  they give none.
- Types as for helpers: `int`, `float`, `bool`, enums; a `Priority` parameter is the level.
- **One request at a time.** The fields are the entry's, not the request's: a second request that
  writes them before the first has run, or reads the result after another request ran the entry,
  sees the other's values. [ATL2008](analyzers.md#atl2008) warns where the compilation requests such
  an entry from more than one method, more than once without waiting for the end, or in a loop
  without waiting.
- Works in vanilla source as well (a script compiled whole gets the fields at the end of its shared
  data). Decompiled code has none: the original had no parameters.

## Variables

Variables are static fields of the script class, typed by the variable's type, with their storage
area and offset:

```csharp
[Var(Storage.Save, Save.StoryProgress)] public static ushort StoryProgress;
[Var(Storage.Event, 0x0040)] public static float[] Heights = new float[4];
[Var(Storage.Private, 0x0010)] public static byte[,] Grid = new byte[3, 8];
```

| `Storage` | Area | Notes |
|---|---|---|
| `Save` (0) | the save game | storage 0 offset = save offset − `0x1ec`; `Save.*` names the known fields |
| `Common` (1) | variables common to all scripts | no names yet |
| `Private` (3) | the worker's private data | each worker its own copy, declared once in the script class |
| `Shared` (4) | the script's shared data | |
| `Event` (6) | the event data | |

- Types: `byte` (`u8`), `sbyte` (`i8`), `ushort` (`u16`), `short` (`i16`), `uint` (`u32`), `int`
  (`i32`), `float` (`f32`); arrays of those, 1-D or 2-D. The size is the initializer
  (`new byte[6]`; a 2-D array's second dimension is the row length). `[Var(…, Length = n)]` is the
  alternative for a 1-D array.
- A variable declared as an array is an array in C#, also with length 1 (`new byte[1]`): the
  variable table has a bit for it.
- Field initializers on a vanilla script's scalars are errors: its initial values are in the data areas
  (`[SharedData(…)]` and the others in `M209.Data.cs`), as hex. A mod gives its own variables initial
  values as initializers and changes vanilla ones in a static constructor ("Initial values and data
  edits" below).
- The order of the fields is the order of the script's variable table. The decompiler names
  variables `V0`, `V1`, …, save variables after the field at their offset
  (`StoryProgress`) or by offset (`Save_0a4a`), with `_2` for a second declaration of the same
  location.
- `Save` holds the storage 0 offsets of 65 save fields named after Fahrenheit's `SaveData`;
  `Story` holds the 509 story progress values the scripts store
  (`Story.S01340_Kami0400`), named by the scripts that store them. Both are `int` constants, so
  `if (StoryProgress < Story.S01340_Kami0400)` and `StoryProgress = Story.S01340_Kami0400;` need no
  cast.
- `At(Grid, i)` is an element of a 2-D array by its flat index; `Addr(ref Heights[2])` is the
  address of an element, for natives that take one (`pushvref`).
- A mod's variables are fields of the script class without `[Var]`: the linker places them in the
  shared data. With `[Var]` they name a fixed location (a save or common variable).

### Registers

A worker's registers are `static` fields with `[Register(n)]`: `int` for integer registers 0-3,
`float` for float registers 0-9.

```csharp
[Register(0)] public static int I0;
[Register(1)] public static float F1;
```

**Available**, with an explicit number. A function worker's register fields alias its caller's
registers. Registers keep their value across waits and across entries of the same worker, so a
register used across a wait while another entry writes it gets
[ATL0001](analyzers.md#atl0001).

`RegA`, `RegB` and `LastResult` are the machine's register A, register B and float accumulator
(`Worker` members). The decompiler writes `RegB = LastResult;` after a native that waits for an
answer (`Common.MsgShowWaitResult(…); RegB = LastResult;`): the answer is in the accumulator.
**Decompiler only.**

**Planned**: `[Register]` without a number (slots in declaration order). For values an entry needs
only while it runs, use a local ("Locals" below).

### Locals

**Available.** `int n = 3;`, `float speed;`, `bool done = Hp < 10;`, `var k = n * 2;`,
`const int Max = 4;`, and `for (int i = 0; i < 4; i++)`. A local is `int` (also `uint`, `short`,
`ushort`, `sbyte`, `byte`, an enum, or another integer type of the reference assembly, all held as
32-bit integers), `float` or `bool`; not an array, `long`, `double` or `string`.

The compiler decides where each local lives (docs/plan.md, "Variables": locals survive waits):

- in a **register** no code of the worker names (its `[Register]` fields, and those of the
  function workers it calls) and no other local needs at the same time, when the local never holds
  a value across a wait;
- in a **hidden variable** otherwise: a variable of the worker's private data (storage 3) which
  only this local uses, with a copy for each level the worker runs. A wait is a native that polls, a
  request, a call of a function worker or of a helper that waits, `Halt()` and the opcodes not
  understood: while an entry waits, another entry of the same worker may run and change any
  register.

So a local keeps its value across `Common.WaitFrames(1)` or `await`, unlike a `[Register]` field
([ATL0001](analyzers.md#atl0001)). Hidden locals belong to one worker and one level: two monsters of
the same kind in a battle share their script (and its shared data) but not their private data, and a
method that runs on two levels of its worker at once (requested again at a higher level while it
waits) has a copy at each. A helper's hidden locals are those of the worker that calls it. In a mod,
hidden variables are new variables of the package, which the linker places in the private data of the
workers that run the code; in a script compiled whole, those workers' private data grows
([encoding.md](encoding.md#locals)).

## Types and values

- `int` is 32 bits and wraps (C#'s default `unchecked`); `checked` is an error
  ([ATL1010](analyzers.md#atl1010)). `uint`, `short`, `ushort`, `sbyte`, `byte` are the variable
  types; values on the stack are 32 bits.
- `float` is `f32`. Float literals need `f` (`35f`); a `double` literal is an error
  ([ATL1009](analyzers.md#atl1009), with a code fix).
- `bool` exists for conditions. ATEL has no `bool` type: a comparison pushes 0 or 1, and a
  condition tests any value against 0.
- Enums of the reference assembly type native parameters: `Target`, `ActorProperty`, `Command`,
  `BattleMotion`, `Model`, `Map`, `Sfx` (`Btl.GetActorProperty(Target.Own, ActorProperty.Hp)`,
  `Common.SetModel(Model.Tidu)`, `Common.MapJump(Map.Kami0000, 0)`). A member compiles to its
  number; a cast (`(Target)V2`, `(Sfx)0x80000017`) compiles to nothing. `Sfx` has no members yet:
  sound effects are written as casts, in hex.
- Battle ids are `int` constants, `Battle.Kami00_00` (`Btl.LabelExe(Battle.Kami00_00, 1)`,
  `if (Btl.GetBattleId() == Battle.Kami00_00)`): the scripts keep them in `int` registers, so an
  enum would need casts there. A native parameter or result that takes them is marked
  `[Values(typeof(Battle))]`.
- `Text[12]` is a message ID (`TextId`, see "Text"); an `int` converts to `TextId` implicitly.
- ATEL has no conversions, so every cast compiles to nothing. Where C# needs one the decompiler
  writes it: `(int)` for a float passed to an `int` parameter, `(byte)(V2 + I0)` to store an `int`
  into a `byte`, `unchecked((byte)-65)` for a constant out of range.

## Natives

Natives are static methods of `Common`, `Calc`, `Screen`, `Chr`, `Cam`, `Btl`, `Fx`, `Movie`,
`Group9`, `GroupC` and `GroupD` (`src/Ffx.Atel.Game/Natives.g.cs`, generated from
`data/natives.json`). Each has `[Native(id)]` (`Polls = true` for natives that wait) and an XML doc
comment with what is known about it, which the IDE shows on hover. The 92 most used have a summary
written for modders and named, described parameters; what reverse engineering found about them is
in `<remarks>`.

```csharp
Common.WaitFrames(30);
if (Btl.GetActorProperty(Target.Own, ActorProperty.Hp) < 100)
{
    Btl.UseCommand(Target.Own, Command.Thundara);
}
```

- A call as a statement is `func_facc` (the result goes to the float accumulator); a call whose
  result is used is `func`.
- Arguments are positional or named (`Common.WaitFrames(frames: 30)`,
  `Common.MoveSetFrames(frames: 2f, elapsed: 1f)`): a named argument binds to the parameter of that
  name. Arguments are evaluated in the order they are written, as in C#; out of the parameters'
  order they go through temporaries first ([encoding.md](encoding.md#new-code)).
- 25 natives that are predicates return `bool` (`Common.VoiceIsPlaying()`,
  `Common.PartyIsJoined(…)`); the others return `int` (or `float`), also when the scripts only
  test them.
- Many natives still have generated names (`Btl.ActorSetField505`, `Common.MsgGetText_0236`) and
  untyped parameters (`a0`, `a1`). Names and types come from `data/native-types.json` and the
  generator `tools/gen-natives.cs`.
- The natives most scripts call are also members of the worker and of static classes (`Pos = …`,
  `Turn.Start(…)`, `Wait.Frames(…)`): see "Members". The decompiler writes those.

## Members

**Available.** The natives most scripts call are also members of the worker, of a component of the worker, or
of an engine-wide static class, so that the source reads as object-oriented C#. A member is its native: the
compiler lowers it onto exactly the call the native's form gives, and the decompiler writes the member wherever
the bytes are that call. The native's form (`Common.SetPos(…)`) stays valid everywhere.

```csharp
// bsvr0000, Rikku.E8Async, as the natives:            // and as members:
Common.SetPos(4.623341f, -19.153864f, 587.17316f);      Pos = new Vector3(4.623341f, -19.153864f, 587.17316f);
Common.WorkerStopMove(Common.GetWorkerId(-1));          Move.Stop();
Common.SetYaw(-0.307183f);                              Yaw = -0.307183f;
Chr.SetNeckSpeed(20);                                   Neck.Speed = 20;
Chr.SetNeckTarget(10);                                  Neck.Target = 10;
Common.WaitFrames(95);                                  Wait.Frames(95);
Common.TurnSetYaw(1.572062f);                           Turn.Yaw = 1.572062f;
I3 = Common.GetTurnKind();                              I3 = Turn.GetKind();
Chr.SetMotion(Common.GetTurnMotion(I3));                Motion = Turn.GetMotion(I3);
Common.TurnSetFrames(0f, Common.GetTurnFrames(I3, 15, 30));   Turn.SetFrames(0f, Turn.GetFrames(I3, 15, 30));
Common.TurnStart(0, 4099, 0);                           Turn.Start(0, 4099, 0);
Common.TurnWait();                                      Turn.Wait();
Chr.ReleaseMotion();                                    Motion.Release();
Chr.PlayMotion(0x71050, 0, 1, 0, -1);                   Motion.Play(0x71050, 0, 1, 0, -1);
Chr.WaitMotion();                                       Motion.Wait();
```

| Form | Is | Example |
|---|---|---|
| a property of the worker | a native on this worker; `=` calls the setter's native, a read the getter's | `Yaw = 1.5f;`, `F0 = Yaw;`, `Hidden = true;`, `Model = Model.Rikk;` |
| a `Vector3` property | the native's three float arguments, written as `new Vector3(x, y, z)` (or `new(x, y, z)`); `.X`, `.Y`, `.Z` read through the natives that read one | `Pos = new Vector3(1f, 2f, 3f);`, `F0 = Pos.Y;` |
| a method of the worker | the native's call | `EnableTrigger(0);`, `DetachAndDispose(1);` |
| a component | the worker's `Turn`, `Move`, `Motion`, `Neck`; its members are natives | `Turn.Start(0, 4099, 0);`, `Move.Target = new Vector3(…);` |
| `Motion = id` | the component takes an assignment: `Chr.SetMotion(id)` | `Motion = Turn.GetMotion(I3);` |
| an indexed group | the native's first argument is the index | `Sprites[3].Show(V12);` |
| a static class | an engine-wide native | `Wait.Frames(30);`, `Camera.Roll = 5f;`, `Sound.Play(sfx);` |
| another worker | a native whose first argument is a worker: its class, or `Workers[n]` | `Tidus.AutoTalk = true;`, `F0 = Tidus.Pos.X;`, `Workers[V3].Turn.Stop();` |

- **This worker, another worker.** A native that takes the worker it acts on is reached three ways:
  written alone it acts on this worker and passes `Common.GetWorkerId(-1)`, the scripts' own idiom
  (`Move.Stop()` is `Common.WorkerStopMove(Common.GetWorkerId(-1))`); `Tidus.Move.Stop()` passes Tidus's number
  (its position in `[Workers]`); `Workers[n].Move.Stop()` passes `n`. Where the game has a native of its own for
  this worker (`Yaw` reads `GetYaw`, `Tidus.Yaw` reads `GetWorkerYaw`), the member alone is that one.
- **Properties are statements and values, as the natives are.** A setter is a statement (`Yaw = x;`); its
  native's result is not read (where a script reads it, the source keeps the native's call). `+=` and the other
  compound assignments are errors on a property: they would be two natives. A property without a getter
  (`Pos` has none for the whole vector) is only assigned.
- **`bool` properties** (`Hidden`, `BodyHit`, `GroundFollow`, `AutoTalk`, `Neck.Enabled`) take `true` and
  `false` (1 and 0) or a condition; any other value as `AsBool(v)`.
- `Vector3` is `Ffx.Vector3`, not `System.Numerics.Vector3` nor Unity's: ATEL has no structs, so a vector is
  never stored, compared or computed with. It only ever is the value of a property, as `new Vector3(x, y, z)`,
  whose three values go to the native as written; reading one is by component. A .NET vector would offer
  arithmetic and methods no script can run, and need a `using` the decompiled files do not have. Anything else
  with a `Vector3` is [ATL1001](analyzers.md#atl1001).
- Members are `protected static` members of `Worker` (requested entries are static, and there is one
  instance of each worker): `Tidus.Yaw` is C#'s static member access through Tidus's class, which the compiler
  reads as Tidus's number. In C# a member of another worker that the natives only have for this worker
  (`Tidus.Pos = …`) compiles; the Atel compiler reports it ("acts on this worker only").
- A member is a member of the worker: a script-class helper (`static partial class M209`) has none; it calls
  the natives.
- **Names.** A member's name is its native's verified name, regrouped (`TurnSetYaw` is `Turn.Yaw`,
  `WorkerStopTurn` is `Turn.Stop()`), or the native's documented meaning (`Chr.Hide(1)` is `Hidden = true`).
  Natives whose meaning is not verified keep their own names and stay `Group.Native(…)` (docs/plan.md, "Only
  verified names"). Each member's doc comment is its native's, with the native it calls.
- **Generated.** `data/native-members.json` lists the natives that are members (path, getter or setter,
  whether the first argument is a worker, whether the value is a `Vector3` or a `bool`);
  `dotnet run tools/gen-members.cs` writes `src/Ffx.Atel.Game/Members.g.cs` from it and `data/natives.json`.
  The compiler and the decompiler read the members from the reference assembly
  (`Ffx.Atel.Compiler.NativeMembers`). 88 natives are members today, 65 % of the native calls in the shipped
  scripts; [research/oo-surface.md](research/oo-surface.md) has the numbers and what comes next.
- **Inline helpers** may be written with members: an `[Inline]` method of a worker (or, in the reference
  assembly, of `Worker`, called without a class name) that says `Turn.Yaw = yaw; Turn.Start(0, 4099, 0);
  Turn.Wait();` expands to the natives' calls as any inline method does.

The groups and their members:

| Group | Members (native) |
|---|---|
| the worker | `Pos` (`SetPos`; `.X` `.Y` `.Z`: `GetWorkerX/Y/Z`), `Position` (`SetPosition`), `Yaw` (`SetYaw`, `GetYaw`, another's `GetWorkerYaw`), `Pitch`, `Model`, `PartyIndex`, `MoveSpeed`, `TalkRange`, `TalkAngle`, `Hidden` (`Chr.Hide`), `BodyHit`, `GroundFollow`, `PushWeight`, `GroundMode`, `AutoTalk` (`SetAutoTalk`, another's `SetAutoTalkOf`), `EnableTrigger`, `DisableTrigger`, `Detach`, `DetachAndDispose` |
| `Turn` | `Yaw`, `Pitch`, `Speed`, `Speed2`, `SetFrames`, `Start`, `Wait`, `Stop` (any worker), `GetKind`, `GetMotion`, `GetFrames`, `SetMotions` |
| `Move` | `Target` (`Vector3`), `Speed`, `ArriveRadius`, `Yaw`, `SetFrames`, `Start`, `Wait`, `Stop` (any worker), `StopAllLevels`, `YawToTarget`, `PitchToTarget` |
| `Motion` | `= id` (`SetMotion`), `Current`, `Play`, `Wait`, `Release`, `PlayDefault`, `SetAuto`, `Blend`, `NextBlend`, `Speed` (any worker), `Frame` (any worker) |
| `Neck` | `Enabled`, `Target` (a worker's number), `TargetPos` (`Vector3`), `TargetCamera`, `Speed`, `SetRot` |
| `Sprites[slot]` | `Show`, `Hide`, `SetPos`, `SetScale`, `SetColor` |
| `Wait` | `Frames`, `FramesUnlessHddMode`, `Forever`; `Seconds` (below) |
| `Camera` | `Pos`, `LookAt` (`Vector3`), `Roll`, `ScreenDepth`, `SetPolar`, `MoveEased`, `FocusTimeEase`, `Wait`, `FocusWait`, `MoveFinish`, `FocusCut` |
| `Sound` | `Play(sfx)`, `Play(sfx, pan, volume)` |

### Waiting in seconds

**Available.** `Wait.Seconds(s)` waits `s` seconds of game time. The compiler writes it as
`Wait.Frames(n)`, with n = s × `Wait.FramesPerSecond` (30) rounded to the nearest frame, halves away
from zero, so the script holds the same bytes as with `Wait.Frames(n)`:

```csharp
Wait.Seconds(1.23f);        // Wait.Frames(37): 36.9 frames
Wait.Seconds(0.25f);        // Wait.Frames(8): 7.5, a half goes up
Wait.Seconds(2);            // Wait.Frames(60)
const float Pause = 1.5f;
Wait.Seconds(Pause);        // Wait.Frames(45)
```

- A frame is one tick of the world update. FFX.exe runs it 30 times a second on the field, in events
  and in battle, whatever it draws. The main menu draws at 60 fps but stops the world, so scripts
  never tick at 60 ([research/frame-timing.md](research/frame-timing.md)).
- `s` is a constant: a literal, a `const`, or `+ - * /` of them, computed as C# does (`3 / 2` is 1).
  Anything else is [ATL3005](analyzers.md#atl3005): the frames are counted at compile time.
- At least one frame: less than half a frame (1/60 s), 0, or a negative value is
  [ATL3006](analyzers.md#atl3006). A script runs once per tick, so it has no shorter wait;
  `Wait.Frames(0)` waits one frame too.
- Game time: twice or four times as fast with the PC's speed-up booster, slower when the PC cannot
  keep up. While a movie with a camera plays, a frame wait counts the movie's frames (29.97 a second).
- The decompiler writes `Wait.Frames`; the bytes do not say whether the source said seconds.
- It is a statement (it gives no value), and may be used in an `[Inline(Waits = true)]` method with a
  constant argument (not a parameter).

## Text

| Form | Status |
|---|---|
| `Common.MsgSetText(0, Text[12]);` | **available**: `Text[n]` is message `n` of the script's table; compiles to the number |
| `Common.MsgSetText(0, V3);` | **available**: a computed index converts from `int` |
| `Dialogue.Say(Text[12]);` | **available**: a line of dialogue as one call ("Inline methods") |
| `Text.New("Nice throw!")` | **available in mods**: a new message; the loader gives it the next free index of the script's tables when it links, the same in every language ([mod-package.md](formats/mod-package.md#messages)) |
| `Debug.Print($"hp = {Hp}")` | **available**: a line in Fahrenheit's log, through native `0xf000` the loader registers (native group F); text with up to 16 interpolated values ([debug.md](formats/debug.md)) |
| `Debug.Break()` | **available**: logs the worker's registers through native `0xf001`, and stops in a .NET debugger if one is attached |

`string` is an error everywhere except attribute arguments and the reference-assembly members that
take one (`Asm`, `Debug.Print`, `Text.New`) ([ATL1002](analyzers.md#atl1002)). `Debug.*` calls compile to the
loader's natives by default; `--debug drop` (or `--no-debug`) leaves them out with a warning, and
`--debug error` refuses them. The editor extension shows each `Text[n]`'s
message from your own game's tables ([editor.md](editor.md)).

`Text.New` takes one string literal, written as `atel text export` writes messages (control codes in
braces, `{{` and `}}` for a brace); the same text twice is one message. The mod's `mod.toml` names the
language it is in (`source_language`, default `en`): that language's table gets the text, every other
language's a copy of it, which the loader's report lists as untranslated. Only events and battles have
tables; in a monster's script, or in a script compiled whole (`atel compile`, which has no linker to
number it), `Text.New` is an error.

## Statements and expressions

**Available:**

| C# | Notes |
|---|---|
| `x = e;` | a variable, array element or register |
| `x += e;` and `-= *= /= %= &= \|= ^= <<= >>=` | on a scalar the same code as `x = x + e` |
| `Native(…);` | a native call as a statement |
| requests, `W48.Run();` | see above |
| `return;` | `end`, or `ret` in a function worker |
| `+ - * / %`, `<< >>`, `& \| ^ ~ !`, `== != < > <= >=` | `>>` is arithmetic (`sar`) |
| `-5`, `-1.5f` | a negative literal: the negative constant |
| `-x` | any other value: `neg` for a float, `0 - x` for an integer |
| `x++`, `++x`, `x--`, `--x` | on any variable, element, register or local; as statements and as values |
| `c ? a : b` | only the chosen branch is evaluated |
| `a && b`, `a \|\| b` | short-circuit, as in C#: `b` is evaluated only when `a` does not decide |
| `int n = e;` | locals (above) |
| `true`, `false` | inside a condition: 1 and 0 |
| `Type.Member` | an `int` constant of the reference assembly or an enum member: its number |

Operands are evaluated left to right, as in C#, also where a later one needs statements of its own
(`Out = Hp++ * 10 + ++Hp;`, `Table[i++] += Foo();`): the compiler evaluates the earlier ones into
temporaries first. How each of these lowers is in [encoding.md](encoding.md#new-code).

**Not compiled yet** (the compiler reports "not a statement/expression Atel compiles"): `foreach`,
an assignment used as a value (`a = b = 0`).

**Not Atel at all** (analyzer errors, [analyzers.md](analyzers.md)): `new` for objects and local
arrays (ATL1001), `string` (ATL1002), the .NET base library (ATL1003), collections (ATL1004),
LINQ (ATL1005), exceptions (ATL1006), lambdas, delegates and local functions (ATL1007), generic
declarations (ATL1008), `long`, `double`, `decimal`, `char`, `object` (ATL1009), `checked`
(ATL1010), recursion (ATL3001), `call` deeper than 3 (ATL3002), and `async` methods that do not
return `Ffx.Atel.Task` (ATL2007).

### Conditions

C# needs a `bool` where ATEL takes any value. The compiler decides by the syntax alone whether an
expression is a condition: a comparison, `!`, `true`/`false`, `AsBool(…)`, a native that returns
`bool`, or `&`, `|`, `^` of conditions.

- **`&` and `|` evaluate both sides.** On two conditions they are ATEL's `land` and `lor`, which do
  not short-circuit; C#'s `&` and `|` on `bool` do the same, so the meaning is unchanged. On
  integers they are the bitwise `and`/`or`. The decompiler writes these, as the original never
  branched inside a condition.
- **`&&` and `||` short-circuit**, as in C#. In a condition (`if`, the loops, `if (…) goto`) they are
  jumps, one test per operand; as a value (`AsInt(a && b)`, a `bool` local) they set a temporary to
  0 or 1. New code may use either form; mind that `&` evaluates a native call on its right also when
  the left is false.
- A `bool` local is a condition, like a comparison.
- `AsBool(x)` is an `int` (or `float`) used as a condition, `if (AsBool(V3 & 4))`: no code, the
  machine tests against 0. `if (V3 != 0)` would add an `ne`, so different bytes. **Decompiler
  only**, but fine in new code.
- `AsInt(c)` is a condition used as a value, `V3 = AsInt(I0 > 2);` (0 or 1): no code.
- Constant conditions: `if (false) { … }` is code that is jumped over (the original kept such dead
  code), `while (true)` a loop without a test. Any other constant condition is an error.
- `if (Cut.ZanarkandCrowdFacingReaction) { … }` is `if (false)` with a name: a `const bool` of
  `Ffx.Cut`, all `false`, for dead blocks whose code is understood
  ([research/dead-code.md](research/dead-code.md#names)). Only as the whole condition of an `if`.

### Control flow

**Available:** `if`/`else`, `while`, `while (true)`, `do … while`, `for`, `switch`, `break`,
`continue`, `goto case`, `goto default`, `goto lN;` with labels `lN:`.

- `for` may declare locals (`for (int i = 0; i < 4; i++)`) or use registers and variables
  (`for (I0 = 0; I0 < 4; I0 += 1)`, as the decompiler writes it).
- `continue` works in `while` and `for` (it goes to the step); not in `do … while`.
- `switch` takes any `int` expression. Each section has exactly one label (no `case 1: case 2:`):
  a constant `case 3:`, a relational pattern `case < 5:`, `case >= 10:`, or a range
  `case >= 380 and <= 401:`. No `when`. `default` may be anywhere. The decompiler names story values
  in cases (`case >= Story.S02400_Nagi0000 and <= Story.S02875_Zkrn0600:`).
- **Fall-through.** C# requires a section to end in a jump. Where the original runs from one section
  into the next, the section ends in `goto case k;` (or `goto default;`) naming the next section,
  which compiles to no code. Any other `goto case` is a jump.
- **Labels** are `l` plus a number (`l12:`), and are the worker's label numbers. The decompiler
  keeps `goto` only where no structured statement fits (8,448 in the corpus: jumps out of nested
  constructs to a shared end, or to an outer loop's head). `if (c) goto l3;` without braces is one
  conditional jump.
- `[Labels(4, 2, 3)]` on a method pins the numbers of its labels where the original compiler
  numbered them otherwise than the rule (26 methods in the corpus). **Decompiler only.**

## Machine-level helpers

`Worker` members that compile to fixed bytecode. They exist so that every vanilla script
decompiles to C# that compiles back to the same bytes; most are **decompiler only**. All compile to
the instructions shown or to nothing.

| Member | Bytecode | Why it exists |
|---|---|---|
| `AsBool(x)` | none | an `int` as a condition; C# needs a `bool` |
| `AsInt(c)` | none | a condition as a value; C# needs an `int` |
| `At(a, i)` | `pushva` | a 2-D array indexed by its flat index |
| `Addr(ref a[i])` | `pushvref` | the address of an element, for natives |
| `StoreClamped(ref x, v)` | `popv_1`, `popva_1` | a store that clamps to the variable's range (8 in the corpus) |
| `Pooled(index, value)` | `pushc`, `pushfc` with that index | a pinned pool that holds the value twice, where the code uses the later copy (1 script) |
| `Nop(n)` | `nop_76 n` | a no-op with an operand the original compiler left (38 in the corpus) |
| `Entry(n);` | none | entry `n` starts here, inside a method holding several entries' code |
| `Request`, `RequestWaitStart`, `RequestWaitEnd`, `RequestParty` | `req*`, `op_39` | requests the call form cannot express |
| `Call<W>()` | `call` | the older form of `W.Run()` |
| `Halt()`, `Breakpoint()` | `op_54`, `op_40` | |
| `End(v)`, `End(v, reset: true)` | `endp`, `endp_1` | |
| `WaitForEntry<Lulu>(5)`, `WaitForEntry(worker, entry)` | `op_77` | wait until the worker has no request for the entry queued or running (`_ = Lulu.E5Async(); WaitForEntry<Lulu>(5);` waits for it to end); goes on at once when there is none |
| `WaitForPartyEntry(slot, entry)` | `op_78` | the same for the worker of a party slot (0-2); unused by the shipped scripts |
| `Op77(a, b)`, `Op78(a, b)` | `op_77`, `op_78` | the older names of the two above |
| `SetTriggerEntry(Addr(ref V38[0]), kind, entry)` | `op_79` | the entry trigger `kind` (0 talking to the worker, 3 touching it, 4 crossing its line) requests of this worker; the worker's table of eight is kept in the variable (a `ushort[8]`), filled with its type's defaults (`2 3 4 5 …`) by the first call |
| `Op79(table, kind, entry)` | `op_79` | the older name of `SetTriggerEntry` |
| `RegA`, `RegB`, `LastResult` | `pusha`/`popa`, `pushb`/`popb`, `pushfacc` | the machine's registers |
| `Asm("""…""")` | the lines | assembly in `atel disasm` syntax; the decompiler writes none since Phase 4, but it stays valid input |

`Asm` takes one string literal; its `lN:` lines are the worker's labels. A mod cannot refer to a
constant by pool index (`Pooled`, or `pushc`/`pushfc` in `Asm`).

## Layout pins

Attributes on the script class for what the language has no construct for, so that the compiler
gives back the original block. **Decompiler only.** The decompiler puts them on the layout part
(`M209.Layout.cs`), as it puts a worker's `[Worker(…)]` on a part of the worker there; the compiler
reads them from any part of the class.

| Attribute | Pins |
|---|---|
| `[Header(Author = …, Name = …, Word14 = …)]` | the header's strings and the words not yet understood |
| `[Ints(…)]`, `[Floats(…)]`, `[FloatBits(…)]` | the constant pools' order, where the first-use rules fail |
| `[Pin("shared", 0x…)]` | a region at another offset than the layout rules give |
| `[Patch(offset, hex)]` | bytes no rule produces (none in the corpus) |
| `[SharedData]`, `[EventData]`, `[OtherData]`, `[AreaData]`, `[MapData]` | the data areas, as hex |

See [encoding.md](encoding.md) for the rules these pin.

## Layout of code

A worker's code is its methods in order: library class first, then the worker's files in path
order (`W36` before `W36.2`), then declaration order. Code that does not end in a jump or `end`
runs on into the next method, as in the original. Keep that in mind when moving methods around
in vanilla source: it changes the bytes, and can change behaviour.

## Mods

A mod is C# in the namespace of the script it changes, compiled against the decompiled vanilla
source (`atel compile-mod`, [tutorial.md](tutorial.md); the package format is
[formats/mod-package.md](formats/mod-package.md)). A mod's files start with the same usings as the
decompiled ones, `using static` of the script class included.

```csharp
using Ffx;
using Ffx.Atel;
using static Monsters.M999.M999;

namespace Monsters.M999;

partial class M999
{
    public static int Turns;                 // a new variable; the linker places it
}

partial class Larva
{
    [Postfix(nameof(Init))]
    static void MoreHp() { Btl.SetActorProperty(ActorProperty.Hp, 1234); }

    [Prefix(nameof(E2))]
    static void SkipWhenLow()
    {
        if (Btl.GetActorProperty(Target.Own, ActorProperty.Hp) < 10) { return; }
    }

    public static async Task CountAsync() { Turns += 1; }   // a new entry
}

public sealed partial class Helper : BattleWorker  // a new worker
{
    protected override void Init() { return; }
    protected override void Main() { Common.WaitForever(); }
}
```

| Declaration | Does |
|---|---|
| a field of the script class | a new variable (shared data, or a fixed location with `[Var]`) |
| `[Prefix(nameof(M))]` | runs before vanilla method `M`; `return;` ends the entry, so `M` does not run |
| `[Postfix(nameof(M))]` | runs after `M`, wherever `M` ends the entry |
| `[Replace(nameof(M))]` | runs instead of `M` |
| `[Inject(nameof(M), Before = nameof(X), Occurrence = n)]` | runs inside `M`, before (or `After =`) the statement with its n-th call to `X` ("Injecting code" below) |
| an `async` method without attribute | a new entry; its number is given when linking |
| a plain method without attribute | a helper (above): a new function worker of the mod |
| a new class deriving from a worker type (`Actor`, `BattleWorker`, …, or `Worker0` … `Worker6`) | a new worker |
| `[Register(n)]` fields | name registers, as in vanilla |
| `static M999() { … }` in the script class | initial values: the script's data, the monster's stats and rewards ("Initial values and data edits") |
| `static Larva() { … }` in a vanilla worker | initial values of that worker's private data (events only) |
| an initializer on a new field (`public static int Turns = 5;`) | the new variable's initial value |

**Available.** A mod does not change attributes or base classes of vanilla classes. A new worker
has private data of its own with `[Worker(PrivateLength = 0x10)]` (and `[PrivateData(…)]` or its static
constructor for initial values, events only); the mod's own private variables are
`[Var(Storage.Private, offset)]` fields of the script class. Patches apply to every entry the vanilla method starts. Several
mods' prefixes and postfixes on one method chain in load order; two replacements conflict and the
later wins. New messages: `Text.New("…")` ("Text"). **Planned**: `[Before]`/`[After]`.

A patch is not an engine-run entry, so it cannot be `async void` and cannot `await`; request an
entry of your own (`_ = Larva.CountAsync();`) and await there.

### Initial values and data edits

**Available.** A mod changes single values of a script's data without replacing the script or a
method: a variable's initial value, an element of a table, a monster's stats and rewards. It writes them
as a C# static constructor, which is where C# sets a class's static fields before anything uses them:

```csharp
partial class M209
{
    public static int Turns = 5;                 // a new variable with its initial value
    public static byte[] Odds = { 10, 20, 70 };  // a new table; its size is its values'

    static M209()
    {
        MonsterStats.Hp = 4321;                  // the monster's stat record (vanilla 1498)
        MonsterStats.Strength = 30;
        MonsterRewards.Gil = 999;
        V7 = 2;                                  // a shared variable's initial value
        Table[3] = 7;                            // an element of a shared array; Grid[1, 2] for 2-D
        Data.At<ushort>(DataArea.Other, 0x20) = 7;   // bytes no variable covers
    }
}

partial class Boy                                // an event's worker
{
    static Boy() { Steps = 12; }                 // its private data (storage 3)
}
```

| Target | Where it is written |
|---|---|
| a shared (storage 4) or event (storage 6) variable, or an element by constant indices | the script's data area, at the variable's offset |
| `MonsterStats.Hp`, `Mp`, `OverkillThreshold`, `Strength`, `Defense`, `Magic`, `MagicDefense`, `Agility`, `Luck`, `Evasion`, `Accuracy` | the monster file's stat record (section 2) and its copy (section 6) |
| `MonsterRewards.Gil`, `Ap`, `OverkillAp`, `DropChance1`, `DropChance2`, `StealChance`, `EquipmentDropChance` | the monster file's rewards (section 4) |
| `Data.At<T>(DataArea.Shared \| Other \| Event \| Area, offset)` | that area, at that offset |
| a private (storage 3) variable, or `Data.At<T>(DataArea.Private, offset)`, in a worker's static constructor | that worker's private data block; in a mod's new worker (`[Worker(PrivateLength = …)]`) also the mod's own `[Var(Storage.Private, …)]` variables, carried in the new worker's private data |

- Each statement assigns a constant: a literal, a `const`, a game constant (`Story.*`, `Save.*`) or an
  enum member; arithmetic of them is folded. An integer must fit the field's width, a `float` field takes
  any number.
- The battle reads a monster's stats and rewards from its own file, not from the kernel tables (ffx-decomp,
  `docs/formats/monster.md`: `Mon_AttachData`, `MsSetRamChr`). `Agility`, `Luck`, `Evasion` and `Accuracy`
  are Fahrenheit's names; FFX.exe's code for them is not read yet.
- A worker's private data has initial values only in events: in battle the game clears a worker's private
  buffer when it sets the worker up, so a monster's worker cannot set one (an error).
- Save (0) and common (1) variables are not the script's data: their values are the save's and the game's.
- A field set twice in one mod is an error. Two mods that set the same bytes are a conflict in the link
  report, and the later mod in the load order wins; different fields of two mods combine.
- The linker writes the bytes in place: nothing moves, and a mod with only data edits leaves the rest of the
  file as it was ([formats/mod-package.md](formats/mod-package.md#data-edits)).
- [ATL4001](analyzers.md#atl4001) marks a static constructor that does anything else.
- `atel compile` of a whole script does not take a static constructor: a script's own initial values are
  its data areas (`[SharedData(…)]`).

### Injecting code

**Available.** `[Inject]` runs a mod's method at a point inside a vanilla method, not only before or
after it. The point is a statement of the decompiled source, named by a call it holds:

```csharp
partial class Ai
{
    // In OnTurn, after the statement with the 3rd call to Btl.UseCommand.
    [Inject(nameof(OnTurn), After = nameof(Btl.UseCommand), Occurrence = 3)]
    void Exhausted()
    {
        Btl.SetActorProperty(ActorProperty.Hp, 1);
    }

    // Before the statement with the 1st call to Common.Random16.
    [Inject(nameof(OnTurn), Before = "Common.Random16")]
    void Count() { Turns += 1; }
}
```

- **The anchor** is a call as it is written in the source: `nameof(Btl.UseCommand)` or `"Btl.UseCommand"` for a
  native, `"Motion.Play"` for a member, `nameof(Dialogue.Say)` for an inline method, `"W02.OpenAsync"` for a
  request, `"Helper"` for a helper. `Occurrence` (1 by default) counts the calls of that name in the method, in
  the order they stand in the file. A shorter name matches longer ones (`"UseCommand"` matches
  `Btl.UseCommand`). The statement is the innermost one holding the call: for a call in an `if`'s condition, the
  whole `if` with its branches; for one inside a branch, that statement only.
- **Before** runs as the statement is entered, **after** as it is left by its end. Jumps that stay inside the
  statement do not run the code again: a loop's back edge to its head does not repeat the code before the loop,
  and the loop's exit runs the code after it once. A jump from elsewhere to the statement (the `else` of an
  earlier `if` landing on it) runs the code before it. A `return` inside the statement leaves without the code
  after it, as a postfix's `return` does.
- **The method** runs on as before; a `return;` in the injected method ends the entry there, as in a prefix.
- **Stable across re-exports.** The compiler finds the statement in the vanilla source each time the mod is
  built, and the package holds its code range in the method with the method's hash: a mod built on another
  game version's method is left out (a hash mismatch), and a vanilla source written differently (a call folded
  into an inline method) is a build error naming the anchor, not code in the wrong place.
  [ATL4002](analyzers.md#atl4002) shows a missing call in the IDE.
- Several mods' injects, prefixes and postfixes on one method combine: at one point the code after the statement
  that ends there runs before the code before the one that starts there, each in load order. A `[Replace]` of
  the method takes the code an inject points into: the inject is a reported conflict and is left out.
- **Limit**: the linker copies the vanilla method to weave the code in, and a worker has 65,536 label numbers;
  a method with more labels than its worker has left cannot take an inject (in the shipped scripts only the
  1.6 MB method of blitzball's worker 36, 53,203 labels); the link reports it as an error.
