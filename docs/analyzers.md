<!-- SPDX-License-Identifier: MIT -->

# Analyzers

Atel is standard C#; what makes it Atel is the reference assembly (`Ffx.Atel.Game`) and the
rules below (docs/plan.md, "The language"). They are Roslyn analyzers
(`src/Ffx.Atel.Analyzers`, netstandard2.0), so Rider, Visual Studio and VS Code show them while
typing and `dotnet build` reports them; code fixes are in `src/Ffx.Atel.Analyzers.CodeFixes`.
`atel compile --check` and `atel roundtrip --check` run them too (`ScriptCompiler.Check`).

The rules run only in a compilation that references `Ffx.Atel.Game` (it has `Ffx.Atel.Worker`).
Like any C# diagnostic they can be suppressed with `#pragma warning disable ATL0001` or set in
`.editorconfig` (`dotnet_diagnostic.ATL0001.severity = none`).

## Numbering

| Range | What |
|---|---|
| ATL0xxx | the plan's own warnings (ATL0001, registers) |
| ATL1xxx | C# that ATEL cannot run: the subset |
| ATL2xxx | requests between workers |
| ATL3xxx | `call`: functions, helpers and inline methods |
| ATL4xxx | what a mod writes on top of vanilla |

| Id | Severity | Rule | Fix |
|---|---|---|---|
| [ATL0001](#atl0001) | warning | register field used across a wait while another entry writes it | |
| [ATL1001](#atl1001) | error | objects created at run time (`new`) | |
| [ATL1002](#atl1002) | error | `string` outside `Debug.*` | |
| [ATL1003](#atl1003) | error | the .NET base library | |
| [ATL1004](#atl1004) | error | collections | |
| [ATL1005](#atl1005) | error | LINQ | |
| [ATL1006](#atl1006) | error | exceptions | |
| [ATL1007](#atl1007) | error | lambdas and delegates | |
| [ATL1008](#atl1008) | error | generic declarations | |
| [ATL1009](#atl1009) | error | not an ATEL type (`long`, `double`, …) | `1.5` → `1.5f` |
| [ATL1010](#atl1010) | error | `checked` | remove it |
| [ATL1011](#atl1011) | retired | `&&` and `\|\|` compile since 2026-11-02 | |
| [ATL1012](#atl1012) | error | compound assignment to a native member (`Actors[Target.Own].Hp += 10`) | |
| [ATL1013](#atl1013) | error | a member holder used as a value (`var a = Actors[Target.AllPly];`) | |
| [ATL2001](#atl2001) | hint | request neither awaited nor discarded | `_ =`, `await` |
| [ATL2002](#atl2002) | error | request to an entry that is not `async` | |
| [ATL2003](#atl2003) | hint | `await` on something that is not a request | |
| [ATL2004](#atl2004) | error | awaited request to the worker's own entry that never runs: at the same level (refused), a lower one (waits for ever), or entry 0 | |
| [ATL2005](#atl2005) | warning | awaited request to the worker's own entry | |
| [ATL2006](#atl2006) | warning | priority the engine uses (0, 7, 8) | |
| [ATL2007](#atl2007) | error | `async` method that does not return `Ffx.Atel.Task` | |
| [ATL2008](#atl2008) | warning | entry with parameters or a result requested where requests may overlap | |
| [ATL3001](#atl3001) | error | recursion | |
| [ATL3002](#atl3002) | error | `call` deeper than 3 | |
| [ATL3003](#atl3003) | error | inline method calls itself | |
| [ATL3004](#atl3004) | error | argument of an inline method with an effect | a variable first |
| [ATL3005](#atl3005) | error | argument of a `[Constant]` parameter (`Wait.Seconds`) is not a constant | a literal or a `const` |
| [ATL3006](#atl3006) | error | `Wait.Seconds` shorter than one frame (or longer than `int`) | at least `1f / 30` |
| [ATL3007](#atl3007) | error | a library class named as a worker with no or several deriving workers in the script | name the worker |
| [ATL4001](#atl4001) | error | a static constructor that is not initial values | |
| [ATL4002](#atl4002) | error | `[Inject]` names no call or member of its method | |
| [ATL4003](#atl4003) | error | a `[Saved]` field that cannot be saved | |
| [ATL4004](#atl4004) | error | a write to another mod's `[Saved]` field | |

"Hint" is `DiagnosticSeverity.Info`: the IDE shows it as a suggestion, `dotnet build` and `--check`
do not list it.

## On the vanilla source

Every script `atel decompile` writes (all 1,621, in the Phase 6 folder layout) and the library
of shared workers are analyzed by `tests/Ffx.Atel.Analyzers.Tests` (`CorpusTests`, needs
`FFX_GAME_DIR`). Nothing is reported at error level. ATL0001 is reported **136 times**
(2026-11-02; 242 before the shared workers moved into the library, where each is analyzed
once): loop counters and other values the
original keeps in a register across a wait (`Common.WaitFrames(1)`, `Btl.ActorWaitMotion()`) in
an entry, where another entry of the same worker also writes that register. They are what the
rule describes, so the rule stays; the original compiler did not warn. Whether each is a real bug
depends on which levels the entries run at, which the analyzer does not know. The compiler's
corpus test counts them apart.

Since requests are written as calls (Phase 5), ATL2006 is reported **221 times**: vanilla
requests at `Priority.P7_Init`, the level `Init` and the battle system run at. The original
scripts do this on purpose; the warning is for new code. The compiler's corpus test counts these
apart too. Nothing else fires on vanilla source.

## The rules

### ATL0001

**Register field used across a wait while another entry writes it** (warning).

A worker has one set of registers (`i0`-`i3`, `f0`-`f9`) for all its entries; a level keeps only
registers A, B and the float accumulator. While an entry waits, an entry on a higher level can run
on the same worker and overwrite a register. ATL0001 reports a wait where a `[Register]` field
holds a value: written before the wait on some path, read after it before being written again;
and another entry of the same worker writes the field.

```csharp
[Register(0)] public int I0;

[Entry(1)]
void E1()
{
    for (I0 = 0; I0 < 10; I0 += 1)
        Common.WaitFrames(1);   // ATL0001: I0 across this wait, and E3 writes it
}

[Entry(3)]
void E3() { I0 = 5; }
```

Waits are `await`, natives with `Polls` (`[Native(…, Polls = true)]`) and `RequestWaitStart`,
`RequestWaitEnd`, directly in the method (not through helpers). Liveness is per method, over
Roslyn's control flow graph. Function workers (`FunctionWorker`, `Worker0`) are left out: they run on their
caller's level and registers.

Fix: use a local (the compiler saves locals across waits), another register, or suppress where
the entries cannot interleave.

### ATL1001

**Objects are not created at run time** (error). ATEL has no objects; workers are created by the
game.

```csharp
var t = new Thing();            // ATL1001
Point p = new();                // ATL1001
int[] local = new int[2];       // ATL1001: arrays are fields
static int[] Table = new int[3];  // fine: a field with a fixed size
static Item[] Rewards = { Item.Potion };  // fine
```

Anonymous objects and `stackalloc` too. A .NET type's `new` is reported as ATL1003/ATL1004, a
delegate's as ATL1007. Fix: fields for state; arrays as fields.

One exception: `new Vector3(x, y, z)` (or `new(x, y, z)`) as the value assigned to a property
(`Pos = new Vector3(1f, 2f, 3f);`) creates nothing: the property's native takes the three values
([language.md](language.md#members)). A `Vector3` anywhere else (`var v = new Vector3(…)`) is ATL1001.

### ATL1002

**`string` outside `Debug`** (error). ATEL has no strings: messages are IDs (`Text[12]`), debug
text goes to `Debug.*` (plan, "Text").

A string is allowed in attributes (`[Event("bjyt0400")]`, `nameof`) and as an argument to a
reference-assembly member that takes one (`Debug.Print($"hp = {Hp}")`, `Asm("""…""")`,
`Text.New("…")`). `string` as a field, parameter, local or return type is reported, and any
other string value (a literal, an interpolation, a concatenation) where it is made.

```csharp
string Name = "wakka";          // ATL1002 twice: the type and the value
Asm("pushimm 1");               // fine
```

### ATL1003

**The .NET base library** (error). Members and types of the `System` and `Microsoft` namespaces do
not exist in the game's interpreter.

```csharp
int a = Math.Abs(x);            // ATL1003 at Abs
int n = V.Length;               // ATL1003: System.Array.Length
```

Allowed: `int`, `uint`, `short`, `ushort`, `sbyte`, `byte`, `float`, `bool`, `void`, and
constants (`int.MaxValue`). Types are judged where they are written (declarations, casts,
`typeof`, patterns, `default(T)`), members where they are used. Attributes and `using`
directives are compile-time only and never reported.

### ATL1004

**Collections** (error): types of `System.Collections.*` and other enumerable .NET types, and
collection expressions of a non-array type. ATEL has variables and fixed-size arrays. Fix: an
array field.

### ATL1005

**LINQ** (error): `System.Linq` (`V.Select(…)`) and query expressions (`from x in V select x`).
Fix: write the loop.

### ATL1006

**Exceptions** (error): `try`, `catch`, `finally`, `throw` and exception types. ATEL has none.

### ATL1007

**Lambdas and delegates** (error): lambdas, anonymous methods, local functions, delegate
declarations, .NET delegate types (`Action`, `Func`), method groups as values, function pointers.
ATEL calls only fixed targets. Fix: a helper method.

### ATL1008

**Generic declarations** (error): a generic type or method declared in source. Each method is one
ATEL body; the reference assembly's generics (`Call<W>()`, `At(…)`, `Task<T>`) are fine.

### ATL1009

**Not an ATEL type** (error): `long`, `ulong`, `double`, `decimal`, `char`, `object`, `dynamic`,
`nint`, `nuint`, pointers; in declarations, casts, and literals (`1.5`, `2L`). ATEL values are 32
bits: `int`, `uint`, `short`, `ushort`, `sbyte`, `byte`, `float`, `bool`, enums, fixed-size
arrays.

Fix (code fix): a `double` literal gets its `f` (`1.5` → `1.5f`).

### ATL1010

**`checked`** (error): `checked(…)` and `checked { … }`. ATEL integers wrap; `unchecked` is fine.
Fix (code fix): remove `checked`.

### ATL1011

**Retired.** It reported `&&` and `||` while the compiler could not compile them. They compile now,
with C#'s short-circuit (language.md, "Conditions"; encoding.md, "New code"), so the rule and its
code fix are gone; the id is not reused.

### ATL1012

**Compound assignment to a native member** (error, 2026-11-09). A property of the object-oriented surface is its
native: reading it calls the getter, assigning it the setter (language.md, "Members"). `+=`, `++` and the other
compound assignments would hide both, so the compiler takes only `=`; write the read and the write out.

```csharp
Actors[Target.Own].Hp += 10;                              // ATL1012
Actors[Target.Own].Hp = Actors[Target.Own].Hp + 10;       // the getter, then the setter
```

### ATL1013

**Member holder used as a value** (error, 2026-11-09). What holds members (`Actors[Target.AllPly]`, `Self`, `Turn`,
`Sprites[3]`, `Workers[n]`) names the receiver of the native a member calls, and the compiler reads it together with
the member after it. There is no object behind it to keep in a local, pass or return.

```csharp
var party = Actors[Target.AllPly];                         // ATL1013
Actors[Target.AllPly].Defense = 0;                         // a member after it
```

### ATL2001

**Request neither awaited nor discarded** (hint, as C#'s CS4014).

```csharp
Lulu.LookAtWakkaAsync();          // ATL2001
_ = Lulu.LookAtWakkaAsync();      // req: queue it and carry on
await Lulu.LookAtWakkaAsync();    // reqe: wait until it ends
```

Fix (code fix): `_ =`, or `await` in an `async` method.

### ATL2002

**Request to an entry that is not `async`** (error). Calling an entry (`[Entry(n)]`, an
`override` of a base class entry) requests it; only `async Task NameAsync()` entries can be
requested from code. `base.Main()` in an override is not a request.

```csharp
Lulu.E3();                        // ATL2002
```

### ATL2003

**`await` on something that is not a request** (hint). Only a request (`Task`, `Task<T>`, their
`.Finished` and `.Started`) waits for another worker; anything else of the reference assembly
completes at once and the compiler drops the `await`. (.NET awaitables are ATL1003.)

### ATL2004

**Awaited request to the worker's own entry that never runs** (error). A worker starts a request only
when its level is above the running one; `Init` runs at level 7, `Main` at level 0. What an awaited
request (`reqe0`) to the worker's own entry does, from FFX.exe ([corpus.md](corpus.md#requests)):

| The request's level | What happens |
|---|---|
| above the caller's | it runs at once and the caller waits for its end: a call (not reported) |
| the caller's | refused, as that level's own request is still queued (always so in `Init`, `Main` and a requested entry): `reqe0` gives 0 at once, the entry never runs and nothing waits. Only after `end1`/`endp1` reset the level does it register, and then never starts while the caller waits for ever |
| below the caller's | refused (0 at once) if that level has a request queued already; else it never starts, and the caller waits for ever (the worker stays at that level; its `Main` never runs again) |
| any, to entry 0 | it never registers: 0 at once, the entry does not run |

All but the first are bugs, so all are errors; the message says which. (Before 2026-11-09 this rule
said the caller always waits for ever; at the same level it does not.)

Reported where both levels are known: the decompiler's raw forms `RequestWaitEnd(level, worker,
entry)` and `RequestWaitStart(…)` with constant operands, to the caller's own worker (its index in
`[Workers]`), from code that runs as entry 0 (`Init`, level 7) or entry 1 (`Main`, level 0): the
entries on the method (`[Entry(n)]`, an `Init`/`Main` override) and the `Entry(n);` marks before
the call.

```csharp
[Entry(0)]
void E0() { RequestWaitEnd(7, 0, 3); }   // ATL2004 (in worker 0): level 7 from Init's level 7, refused
[Entry(1)]
void E1() { RequestWaitEnd(1, 0, 3); }   // fine: level 1 above Main's level 0, a call
```

Fix: a helper method, or a higher level.

### ATL2005

**Awaited request to the worker's own entry** (warning): `await OwnEntryAsync()` (or its
`.Finished`/`.Started`), where the levels are not known. It runs only if the request's level is
above the caller's; at the same level it is refused and below it the caller waits for ever
(ATL2004). Fix: a helper method, which runs synchronously.

The decompiler's raw forms are only checked where the levels are known (ATL2004): vanilla awaits
its own entries 130 times, outside `Init` and `Main` at levels the analyzer cannot see.

### ATL2006

**Priority the engine uses** (warning). Scripts request at levels 1-6 (plan, "Requests between
workers"): 0 is `Main`'s, 7 `Init`'s and the battle system's, 8 unused.

- A `Priority` enum value of the reference assembly (when it has one) that is 0, 7 or 8.
- A constant `level` of `Request`, `RequestWaitStart`, `RequestWaitEnd`, `RequestParty` that is
  0 or 8 or more. Level 7 is not reported there: the shipped scripts request at level 7 260 times.

### ATL2007

**`async` method that does not return `Ffx.Atel.Task`** (error). An `async` method is a
requestable entry, and its return type is the request: `async Task NameAsync()` or
`async Task<int> NameAsync()` with the reference assembly's `Task`. (`async void`; a .NET `Task`
is ATL1003.) The exception is an entry the engine runs rather than another worker
(`[Entry(n)]` or an `override` of a base class entry): it may be `async void`, so that it can
`await` requests, since nothing waits on its result. The decompiler writes such entries so
since Phase 5 (requests as calls).

### ATL2008

**Entry with parameters or a result requested where requests may overlap** (warning). An entry's
parameters and result are hidden fields of its worker (language.md, "Parameters and results"):
written when it is requested, read when it runs; its result written when it ends, read after
`.Finished`. That is safe while one request at a time is pending (plan, "Parameters and return
values"). The rule looks at every request in the compilation to an entry that takes parameters
(besides its priority) or returns a value, and reports each of them when that entry is requested

- from more than one method (their workers may request it at the same time), or
- more than once, and some request does not wait for it to finish (`_ =`, `.Started`), or
- in a loop without waiting for it to finish.

```csharp
int a = await W01.AddAsync(1, 2);         // fine: one method, waits for the end
_ = W01.SetAsync(1);                      // ATL2008: requested twice,
await W01.SetAsync(2).Started;            //          neither waits for the end
```

Fix: await the request to its end, request from one place, or suppress it where the requests
cannot overlap (`#pragma warning disable ATL2008`).

### ATL3001

**Recursion** (error), direct or through other functions and helpers, including function workers
(`Call<W>()`). `call` keeps only a return address and shares the caller's registers.

```csharp
void Again() { Again(); }         // ATL3001: W00.Again → W00.Again
```

Each call on the cycle is reported, with the cycle in the message.

### ATL3002

**`call` deeper than 3** (error). A worker level keeps 3 return addresses (`Script_Run`, level
`+0x1d`). From code that nothing calls (entries), the deepest chain through each call to a
function or helper is followed; a chain of more than 3 is reported at its first call.

```csharp
[Entry(0)] void E0() { A(); }     // ATL3002: E0 → A → B → C → D
void A() { B(); }  void B() { C(); }  void C() { D(); }  void D() { }
```

Calls whose target is only known at run time do not exist in Atel, so the depth is static.

An inline method ([language.md](language.md#inline-methods)) is not a `call`: calls of one count no
level, and ATL3001 does not see them (ATL3003 does).

### ATL3003

**Inline method calls itself** (error). An `[Inline]` method's code is written in place of each call,
so one that calls itself, directly or through other inline methods, would be written out without end.
Reported at each call on the cycle.

```csharp
[Inline] static void A(int n) { B(n); }   // ATL3003: W00.A → W00.B → W00.A
[Inline] static void B(int n) { A(n); }
```

### ATL3004

**Argument of an inline method with an effect** (error). An inline method's argument is substituted
where its parameter is used and evaluated there, perhaps several times or not at all. An argument that
calls something (but `Text[n]`, `Text.New`, `AsBool`, `AsInt`), assigns, increments, or holds `?:`,
`&&`, `||` or `await` would run as often as the parameter is used. An `[Eager]` parameter is used
once, in the method's first statement, so its argument may call a native (`Turn.To(Common.AngleToPoint(…))`);
it still may not assign or increment.

```csharp
Dialogue.Say(Text[12], window: I0);                    // fine: a variable
Dialogue.Say(Text[12], window: Common.Random16());     // ATL3004
int w = Common.Random16();
Dialogue.Say(Text[12], window: w);                     // fine
```

### ATL3005

**Argument must be a constant** (error). A `[Constant]` parameter's value is used when the script is
compiled: `Wait.Seconds(s)` counts its frames from `s` ([language.md](language.md#waiting-in-seconds)).
A literal, a `const` and arithmetic of them are constants; a variable, a register, a parameter or a
call is not.

```csharp
const float Pause = 1.5f;
Wait.Seconds(Pause);                                   // fine: Wait.Frames(45)
Wait.Seconds(F0);                                      // ATL3005
```

### ATL3006

**Wait shorter than one frame** (error). `Wait.Seconds(s)` is `Wait.Frames(n)`, n = s × 30 rounded to
the nearest frame. Scripts run once a frame, so a wait lasts at least one; an `s` that rounds to 0
frames (below 1/60 s), a negative one, or one past 2,147,483,647 frames is an error.

```csharp
Wait.Seconds(1f / 30);                                 // fine: Wait.Frames(1)
Wait.Seconds(0.01f);                                   // ATL3006: 0.3 frames
```

### ATL3007

**Library class that names no worker of this script** (error). In a script's or a mod's code, a library
class's name used as a worker (`MoveTalkDefaults.Run()`, `Call<MoveTalkDefaults>()`,
`WaitForEntry<T>()`, a request) is the one worker of that script deriving from it: the decompiler writes
shared bodies that call a sibling this way, and `atel compile` and `atel compile-mod` read it so. When no
worker of the script derives from the class, or several do, it names none; the compiler stops with the
same message. The library's own code is not checked: its names are resolved in each script that uses it.

```csharp
public sealed partial class W87 : MoveTalkDefaults { }
MoveTalkDefaults.Run();                                // fine: W87.Run()
Idle.Run();                                            // ATL3007: 2 do (A, B): name the worker
```

### ATL4001

A mod's static constructor sets initial values ([language.md](language.md#initial-values-and-data-edits)):
each statement assigns a constant to a field of the script's data, which the linker writes into the
player's file in place. Reported:

- a statement that is not `target = constant;` (a call, `+=`, a loop);
- a value that is not a constant (`Hp = Turns;`);
- an element by an index that is not a constant;
- a target that is no field of the script's data: the mod's own variable (give it an initializer), a save
  or common variable (storage 0, 1), something else;
- a target in the wrong static constructor: private data (storage 3) is set in the worker's, the shared
  and event data and `MonsterStats`/`MonsterRewards` in the script class's;
- private data of a monster's or battle's worker (the game clears it at set-up), `MonsterStats` or
  `MonsterRewards` outside a monster script.

```csharp
static M209()
{
    MonsterStats.Hp = 4321;     // fine
    Hp = Turns;                 // ATL4001: an initial value is a constant
    Mine = 1;                   // ATL4001: private data, set it in the worker's static constructor
}
```

### ATL4002

`[Inject(nameof(M), Before = nameof(X), Occurrence = n)]` runs at the statement with the n-th place of `X` in
the vanilla method `M` ([language.md](language.md#injecting-code)): a call to `X`, or a use of the member `X`
(`nameof(Self.Hp)`), or anything that compiles to a native `X` names. Reported when `M` has fewer places of `X`
(counted as the compiler counts them: in the source, in order), and when the attribute names neither or both of
`Before` and `After`, or an `Occurrence` below 1. The analyzer finds a member's places by name, without the
compiler's lowering, so it may count one more than the compiler (a variable of the member's name, another
property's native call): it reports only what is surely missing, and the build says the rest.

```csharp
[Inject(nameof(OnTurn), After = nameof(Btl.UseCommand), Occurrence = 4)]   // ATL4002: OnTurn has 3 call(s) to Btl.UseCommand, not 4
[Inject(nameof(OnTurn), Before = nameof(Self.Mp))]                         // ATL4002: OnTurn has no use of Self.Mp
```

### ATL4003

A `[Saved]` field ([language.md](language.md#saved-fields)) is a storage 0 variable the loader places in the
save's mod area. Reported for a field that is not static, or is `const` or `readonly`; has `[Var]` or
`[Register]`; is not `byte` … `int`, `float` or a 1-D or 2-D array of one; is an array without its size; is
in a worker, a nested class, or a class that is neither static nor the script class; has a key that is empty
or holds a `/` or a space; has the key of another saved field of the mod. Another mod's field (`Mod = …`) is
reported without its `Key`, and with an initializer (the value is that mod's).

```csharp
[Saved] public static ushort LarvasMet;
[Saved] public static byte[] Flags;                                            // ATL4003: a saved array has its size
[Saved(Mod = "yuta.larvacount")] public static ushort Met;                    // ATL4003: named by its Key
```

### ATL4004

`[Saved(Mod = "…", Key = "…")]` declares another mod's saved field, to read it. Reported on an assignment,
a compound assignment, `++` or `--` of it or of an element of it: the other mod writes its fields. The
compiler refuses the same.

```csharp
Hp = Tally.LarvasMet;
Tally.LarvasMet = 0;                                                        // ATL4004
```
