# Atel# plan

Goal: turn every compiled ATEL script of FFX (events, monster AI, battle
scripts) back into source code that reads like what the developers meant,
and compile that source back into the same bytes. The source language is
**Atel: standard C#, restricted to what ATEL can run**, parsed and checked
by Roslyn and compiled by our own backend to ATEL bytecode. Atel then
becomes the language mods are written in, and a Fahrenheit mod loads those
mods into the existing game (FFX.exe, the Steam HD Remaster).

The plan started in `ffx-rust` (`docs/atel-plan.md`, accepted 2026-11-01)
and moved here when work began.

## Decisions (user)

2026-10-31:

- **Close to C#.** Most modders, including the Fahrenheit community, know
  C#.
- **Byte-identical round trip** is the acceptance bar: decompile, compile,
  and get the original script block and containing file back, for every
  script.
- **The decompiled source lives in a private GitHub repo,
  `yutamago/ffx-atel-src`**, versioned. Claude may create it when the time
  comes. No decompiled output goes into this repo.
- **Mods change things, they don't replace files.** Vanilla files stay
  untouched; mods are composed in load order.

2026-11-01 (replacing the custom syntax of the first version):

- **Standard C#, not a C#-like language.** Files are `.cs`, projects are
  `.csproj`, and Rider, Visual Studio and VS Code work unchanged. The
  subset that compiles to ATEL is enforced by analyzers. Precedent:
  UdonSharp (C# through Roslyn to VRChat's Udon VM), Unity Burst.
- **The tooling is written in C#** (.NET 10): decompiler, compiler,
  analyzers, the game's reference assembly, the mod linker.
- **Mods ship compiled.** Players need neither a compiler nor a decompiler;
  compiled mod packages are linked onto the player's own files.

2026-11-01, at the start of work:

- **Luau is dropped** (it was stage L1 of the ffx-rust rewrite plan).
- **Its own repo, `yutamago/ffx-atel-sharp`** (private), separate from
  `ffx-rust`.
- **For the existing game, not the Rust port.** What runs in the game is a
  **Fahrenheit mod** (the loader): it links the enabled Atel mods onto the
  player's scripts and serves the results. The Rust port may load the same
  packages later; nothing here depends on it.

## What the bytecode says about the original language

From all 758 scripts (17.3 million instructions, 57.9 MB of script
sections; survey 2026-10-31):

- C expressions and types: `+ - * / %`, comparisons, `&& ||` (not
  short-circuit), `& | !`, `<<`; separate signed and unsigned compares;
  variables typed `u8`…`f32` in storage areas.
- Arrays with nested indexing (742k element reads), `&var` passed to
  natives (2k).
- `if` (every conditional forward jump, 405k), loops (64k backward jumps),
  `switch` (selector in register B, compare chain after the case bodies,
  598k backward case jumps; every chain ends in a `jmp`, so `default`
  likely exists).
- Locals in 4 int and 10 float registers per worker; shared subroutines
  (`call`, argument in `i0`); `return value`.
- Workers with numbered entries; requests between workers with 9 priority
  levels (`req` 79k, `reqe` 25k, `reqw` 2.9k).
- Constant folding, small immediates, a breakpoint opcode (a debugger).
- No strings (messages are IDs), no pointers beyond `&var`, no structs.

Phase 0 measured all 1,621 scripts, battle scripts included
([corpus.md](corpus.md)), and adds:

- The stack is empty at every label, jump and end: no `?:` and no
  short-circuit `&&`/`||` inside an expression. Statements are expression
  trees between labels.
- `switch` usually has no `default` (71,181 of 82,809 chains fall through
  to the break label); cases can be ranges (`lo..hi`) and relational
  compares; no two cases share a label.
- 12 % of the instructions are dead code: `if (0) { … }` compiled to a
  `jmp` over code that was kept.
- Labels are numbered per construct as it starts, not in code order.

## The language

Everything below is valid C#; what makes it Atel is the reference assembly
(the game's API) and the analyzers (what ATEL can run).

### Scripts, files and order

- A script is a namespace and a folder, one file per worker:
  `Events/Bj/Bjyt0400/Bjyt0400.cs`, `Wakka.cs`, `Lulu.cs`, each starting
  with `namespace Events.Bj.Bjyt0400;`.
- The main file holds the script class, which names the file and the
  worker order once; a worker's position is its number. Its static fields
  are the variables shared by all workers (storage 6):

  ```csharp
  [Event("bjyt0400")]
  [Workers(typeof(Director), typeof(Wakka), typeof(Lulu))]
  static partial class Bjyt0400
  {
      public static int TimesTalked;
  }
  ```

  Monsters and battle scripts use `[Monster("m001")]` and
  `[Battle("cdsp00_00")]`.
- `partial class` splits a big worker over several files; `using
  Ffx.Battle;` imports the libraries.
- How many scripts go into one project (one compilation) is measured in
  Phase 6: the whole corpus at once is likely too much for an IDE.

### Workers and entries

- A worker is a `class`. Its base class says what it is (`Npc`, `Monster`,
  …; one per worker type, see "Worker types"); function workers are
  `static class`es.
- Entries the engine calls by number are virtual base class members and
  use `override`: `Init` (entry 0, run at priority 7), `Main` (entry 1,
  priority 0), talk, touch, battle turns, … Other entries are `async`;
  plain methods are helpers (see "Three kinds of code").
- Entry numbers are implicit (base class first, then files in path order,
  then declaration order). `[Entry(n)]` only where a specific number is
  wanted; the decompiler writes it only where the rule would differ.
- A base class can provide default entries (a monster without `Main`
  waits for ever), emitted only when the bytes match.

### Worker types

Seven types (worker header `+0x00`), over all 1,621 scripts; the meanings
of 1-6 come from the natives each type calls most ([corpus.md](corpus.md#workers)):

| Type | Workers | Known |
|---|---|---|
| 0 | 441 (events) | **function worker**: every `call` (5,560) targets one, and each has exactly one entry, ending in `ret`; becomes a `static class`. 392 are one shared 39-instruction function |
| 1 | 6,707 (events) | actors and the director: models, motion, movement |
| 2 | 648 (events), 898 (monsters), 2,388 (battle): every monster and battle worker | in events, line triggers and map changes, 8-12 entries, most only `end` |
| 3 | 486 (events) | point triggers, 8-17 entries |
| 4 | 1,944 (events) | no position block; screen and map parts (pad, sprites, map marker) |
| 5 | 1,382 (events) | position block and walk-line table (characters); line triggers |
| 6 | 116 (events) | as 5; point triggers with map sprites |

In events the layout groups the workers by type (`1 4 3 2 5 0` or a
subset, 350 of 397), function workers last in all; the constant pools show
the compiler met them in another order, so worker order is declared, not
derived.

### Three kinds of code

ATEL entries are not functions: each runs on its own worker when that
worker gets to it. The language keeps the original's three kinds apart:

| Kind | Declared as | Runs |
|---|---|---|
| Function worker (type 0) | `static class Common { public static void SetupModels(int mode) { … } }` | synchronously on the caller's worker (`call`) |
| Helper method (new code) | a plain method in a class: `void FaceThePlayer() { … }` | synchronously; the compiler makes it a hidden function worker and `call`s it |
| Entry | `override` (engine-run) or `async Task NameAsync()` (requestable) | on its own worker, through a request |

`call` limits apply to static functions and helpers: at most 3 deep, no
recursion (analyzer error), no `await` on them.

### Parameters and return values

ATEL passes neither: `call` shares the caller's registers, and a request
carries only entry, worker and priority. The compiler provides both:

- **Functions and helpers:** parameters in registers (up to 4 `int` and 10
  `float`, minus those the caller still needs, which the compiler saves),
  the return value in `i0` or `f0`. Synchronous, so always safe.
- **Entries:** parameters and the return value go through hidden fields of
  the receiving worker, written at the request and read after
  `.Finished`. Safe while one request at a time is pending for that entry.
  The compiler sees every caller (requests stay within one script), so it
  warns when two callers could have the same entry pending at once. A
  return value requires waiting for `.Finished`.
- Decompiled code has neither (the original had none); this is for new
  code only.

### Requests between workers

- `Task` and `Task<T>` here are the reference assembly's own task-like
  types (`Ffx.Atel.Task`, with `[AsyncMethodBuilder]`), not .NET's; the
  projects do not import `System.Threading.Tasks`.
- An entry other workers may run is declared `async Task NameAsync()`
  (`async Task<int> NameAsync(int x)` with a parameter and a result).
- `_ = Lulu.LookAtWakkaAsync();` queues it and carries on (`req`).
- `await Lulu.LookAtWakkaAsync();` or `await Lulu.LookAtWakkaAsync().Finished;`
  waits until it ends (`reqe`).
- `await Lulu.LookAtWakkaAsync().Started;` waits until it begins (`reqw`).
- A request without `await` or `_ =` gets a hint (as C#'s own CS4014).
  Requesting an entry that is not `async` is an error.
- A worker can request its own entries (641 `req`, 130 `reqe` in the
  shipped scripts). Awaiting one works only at a priority above the one
  the worker runs at: it interrupts, runs, and the caller continues. At the
  same or a lower priority it cannot start while the caller waits (to
  confirm in Phase 0); the analyzer reports that as an error where it
  knows the caller's priority (`Main` P0, `Init` P7) and warns elsewhere.
  A helper method is usually the better choice.
- Priorities: `Priority.P0_Main` … `Priority.P8_…`, an enum, zero-based,
  sorted by level, names after the number from Phase 0. Scripts push only
  levels 1-7 (level 1 in 78 % of requests, 3 in 14 %), and only the forms
  `req0`, `reqw0`, `reqe0` (plus `req2` 32 times). P1–P6 are for
  scripts; P0 (`Main`'s level), P7 (`Init`, battle system) and P8 (unused)
  compile with a warning. An optional parameter with a common default makes
  most calls omit it.
- `await` on something that cannot be awaited (a native that waits anyway,
  a function or helper) is dropped, with a hint. In C# terms these return
  an awaitable that completes at once.

### Variables

- Fields live in the script's data (storage 2, 3, 4), with initial values
  as field initializers; the script class's static fields in the shared
  area (storage 6).
- `[Register] int Count;` / `[Register(2)] int Count;` puts a field in one
  of the worker's registers; the compiler assigns slots in declaration
  order unless pinned. Function and helper parameters also use registers
  (see "Parameters and return values").
- Locals survive waits, as in C#: where a local is still needed after a
  wait, the compiler saves and restores it through hidden storage.
  Decompiled locals that do not cross a wait stay plain locals; registers
  the original kept across a wait become `[Register]` fields.
- Register fields used across a wait while another entry of the same
  worker writes them give warning ATL0001, suppressible as any C# warning
  (`#pragma warning disable ATL0001`, `.editorconfig`).
- Types: `int`, `uint`, `short`, `ushort`, `sbyte`, `byte`, `float`,
  `bool`, enums, fixed-size arrays. `long`, `double`, `decimal`, `string`
  (outside `Debug`) are errors.
- The save game is the predefined `Save` class, from `ffx-rust`'s
  `docs/formats/save.md` and Fahrenheit's names: the storage 0 offset of each
  known field, which a script's declaration uses
  (`[Var(Storage.Save, Save.StoryProgress)] public static ushort StoryProgress;`,
  Phase 5); unnamed offsets are `Save_0123`. Storage 0 starts at
  `g_SaveData + 0x1ec` (`AtelSetUpCtrlWork`), so save offsets are storage 0
  offsets plus `0x1ec`; story progress is storage 0 `0xa00`.

### Readability

- Natives are static classes and methods (`Msg.Say`, `Cam.Move`,
  `Chr.PlayMotion`); those acting on the worker itself are inherited
  members, called without a prefix (`PlayMotion`). XML doc comments carry
  what is known about each, so the IDE shows it on hover.
- Actor properties are properties: `Radius = 35f;`, `if (Hp < 100)`,
  `Party.Alive.Defense = 0;`. `Position = (x, y, z);` through a tuple
  conversion. Named arguments where they help (`frames: 15`).
- Float literals need C#'s `f` (`35f`); the decompiler writes them so.
- Values are typed by use: `Item[] Rewards = { Item.Potion, … }`,
  `Motion.Idle`, `Command.Bite`, `Target.Party | Target.Alive`.
- Story progress: `Story.S01340_WakkaJoinsParty`, the value zero-padded in
  the name (five digits: the highest is 11000) so alphabetical order is
  progress order; auto-named from the script that sets each value (`data/story-progress.json`: 432 of the 449
  values outside the developer maps have exactly one), renamed by hand.
- Shared behaviour: workers with identical code across scripts (3,525 of
  15,010 are exact copies of another; 7,237 copy another's shape with other
  numbers) become library base classes
  (`class Chest3 : TreasureChest { }`); the compiler copies the code back.
  Near-duplicates may become base classes with parameters.

### Text

- Messages by ID through the worker's `Text` indexer:
  `Msg.Say(Text[12]);`. An editor extension (VS Code, Rider) shows the text
  as inlay hint and hover, in a chosen language, from the player's message
  tables; the formatter can keep an optional `// "…"` comment for reading
  outside an editor.
- New messages: `Text.New("Nice throw!")`. A script holds one index for
  all languages, so every language's table gets the message at the same
  index: the mod's source language (`mod.toml`) gets the text, the others a
  copy marked untranslated. In mods the index is assigned when the
  loader links, so the source keeps `Text.New`; in hand edits of the
  vanilla source the formatter may rewrite it to `Text[173]`. Translators
  work in the per-language TOML (`ffx-rust`'s `ffx text export/import`, or a port of it).
- Debug text is a real `string`, allowed only for `Debug.*`:
  `Debug.Print($"hp = {Hp}");`. It calls new natives in a group the game
  leaves empty (2, 3, a, e or f), provided by the loader mod; without them the compiler refuses it or drops it on request.

### Control flow and semantics

- `if`/`else`, `while`, `do`, `for`, `?:`, `switch` with `case`,
  `default`, `break`, `goto case`; `goto` for flow that has no structured
  form. Code of several entries that jump into each other is one method
  with `Entry(n);` where each further entry starts (see "Phase 4: control
  flow"). Range and relational cases are C# patterns (`case >= 380 and <=
  401:`). Dead code the original kept is `if (false) { … }`, compiled to a
  `jmp` over it.
- How the compiler chooses, from Phase 0: `pushc` exactly for values outside
  `i16`; `func_facc` for a call statement, `func` when the result is used;
  `popa_jz` forward, `popa_jnz` back; labels numbered per construct
  (Phase 4: `while`, `do`: exit, head; `for`: exit, body, head, step;
  `switch`: chain, break, each case's label as its section starts). Pinned
  where no rule covers the original: constant-pool and variable-table order
  (pools match a first-use rule in 77 % (ints) and 71 % (floats) of
  blocks), label numbers the rule does not give (`[Labels(…)]`). The 8
  `popv_1` are `StoreClamped(ref x, v)`, a store that clamps.
- Logic: C#'s `&` and `|` on `bool` evaluate both sides, as ATEL's `land`
  and `lor` do, so the decompiler writes those; `&&` and `||`
  short-circuit as in C# and compile to branches. How an `int` used as a
  condition is written so that it compiles back to the same bytes (`x !=
  0` may or may not have been in the original) is settled in Phase 3.
- `int` is 32-bit and wraps (C#'s default `unchecked`; `checked` is an
  error); `float` is `f32`; unsigned compares follow the operand types.
- Not available, with an analyzer error naming the construct: classes
  with `new`, `string` (outside `Debug`), collections, generics beyond the
  reference assembly's, lambdas and delegates, exceptions, LINQ,
  recursion, anything from the .NET base library.
- Escape hatches for the round trip: `Asm("""…""");`, a raw string in the
  existing assembly syntax, for code the decompiler cannot express yet,
  and attributes that pin layout details (constant-pool order, a
  variable's storage area and offset, unknown header data).

### Phase 2: the source shape

Decided 2026-11-02, when the skeleton round trip went in. Later phases
replace parts of it; until then it is what `atel decompile` writes and
`atel compile` reads.

- **One file per script**, `<Namespace as folders>/<Class>.cs`: namespace
  `Events.<Group>.<Name>`, `Monsters.<Name>` or `Battles.<Name>`, class the
  file name with its first letter upper case (`Events.Bl.Cn_bltz0000`,
  `Events.Ka._200thunder_kami0400`: one event folder can hold several
  scripts, so the namespace ends in the script, not the folder). The
  folder and one-file-per-worker layout is Phase 6.
- **The script class** (`public static partial class`; public, or C#
  warns that the fields are never assigned) carries `[Event("…")]`,
  `[Monster]` or `[Battle]` with the file name, `[Workers(typeof(W00), …)]`,
  `[Header(Author = …, Name = …, Word14 = …)]` for the strings and the
  header words not yet understood, `[Pin("shared", 0x…)]` for layout
  deviations, `[Patch]` for bytes no rule produces (none in the corpus),
  and the constant pools in pool order: `[Ints(…)]`, `[Floats(…)]`
  (`[FloatBits(…)]` when a pool holds a value no float literal gives, a
  NaN payload; none in the corpus).
- **Variables** are its static fields `V0`, `V1`, … in table order, typed
  by the variable's type: `[Var(Storage.Private, 0x0010)] public static int
  V0;`, arrays sized by their length (`public static byte[] V4 = new
  byte[6];`). (Phase 2 wrote `Extra = …` and `Bit24 = true`; Phase 3 reads
  both from the declaration, see below.) The
  table is one per block, so private (storage 3) variables are declared
  here too, not in their workers. Initial values stay in the data areas.
- **Workers** are classes `W00`, `W01`, … in the same namespace; the base
  class `Worker0` … `Worker6` gives the type (function workers stay
  classes for now). `[Worker(PrivateLength = 0x10)]` and `[PrivateData]` for
  the header word and private block.
- **Entries:** one method per distinct entry offset, in code order, named
  after its first entry (`E3`), with every entry there:
  `[Entry(3)][Entry(5)]` (9,780 of 15,010 workers have entries that share
  code). The entry table is in code order in every shipped script, but
  `[Entry(n)]` is always written in Phase 2. Methods are laid out in
  declaration order, so code that does not end in a jump runs on into the
  next method, as in the original. The body is `Asm("""…""")` in the
  syntax of `atel disasm`, labels `lN:` numbered per worker, with each
  native's C# name as a comment.
- **Data areas** (map entrances, shared, other, event, area, private) as
  attributes with a length and hex lines (`HexText`): `offset: bytes` in
  groups of four, zero lines and trailing zeros left out. In a second part
  of the script class at the end of the file, so the declarations come
  first.
- **Text, not `SyntaxFactory`**: the decompiler writes the source as text;
  the shape is fixed and raw strings come out as they read. The compiler
  reads declarations from Roslyn's syntax trees, and checks the source as
  C# with a `CSharpCompilation` against `Ffx.Atel.Game`
  (`atel compile --check`, `atel roundtrip --check`).
- Numbers (2026-11-02): all 1,621 scripts come back byte for byte; 649.5 MB
  of source, of which 7.1 MB data hex and about 605 MB `Asm` lines (21.3
  million instructions; 256 MB of that is indentation); 0 C# errors and
  0 warnings; the whole corpus decompiles, checks and compiles in about
  30 s.

### Phase 3: statements

Decided 2026-11-02, with the expression round trip. What `atel decompile`
writes on top of the Phase 2 shape:

- **One statement per stack run.** The code from an empty stack to the next
  empty stack is lifted into a tree (`Ffx.Atel.Bytecode.Code`: `Stmt`,
  `Expr`) and printed as one C# statement. The compiler reads the C# back into
  the same tree and lowers it (`Lowering`); every tree has exactly one
  encoding. The decompiler lowers each lifted statement again and keeps it
  only if that gives back its own bytes; everything else stays `Asm`, per
  statement (consecutive ones share one `Asm`).
- **Statements:** `V3 = …;`, `V3 += …;` (scalar compound is the same code as
  `V3 = V3 + …`), `V1[i] = …;`, `V1[i] |= …;` (the `op_2b` form), native calls
  `Chr.PlayMotion(…);` (`func_facc`) and in expressions (`func`), with the
  generated names and the argument types of `data/natives.json`;
  `Request(level, worker, entry)`, `RequestWaitStart(…)`,
  `RequestWaitEnd(…)` for `req0`/`reqw0`/`reqe0` and their `popfacc`
  (`variant: n` for `req1`…`req5`); `Call<W03>();`; `return;` (`end`, `ret`
  in a function worker); `Halt();` (`op_54`), `Breakpoint();` (`op_40`),
  `End(x);` (`endp`); `Op77(a, b);` and `Op79(a, b, Addr(ref V3[0]));` for the
  two opcodes not yet understood (13,191 and 1,566 statements). `RegA`, `RegB` and `LastResult` (the float
  accumulator, `pushfacc`) are `Worker` members, so the 3,151 waiting calls
  read as `Msg.Choice(…); RegB = LastResult;`. The forms are
  explicit helpers for now; the `await` forms of "Requests between workers"
  come when entries get names.
- **Control flow stays flat:** `goto lN;`, `if (c) goto lN;` (`popa_jnz`) and
  `if (!c) goto lN;` (`popa_jz`), with C# labels `lN:` at the method level.
  A label is a C# label when every jump to it is a C# `goto` in the same
  method; else it is an `lN:` line in `Asm`, and the jumps to it stay `Asm`
  too (C# has no `goto` into another method, and an unreferenced label is
  CS0164). Phase 4 replaces these with `if`/`while`/`switch`.
- **Registers are fields:** `[Register(1)] public int I1;`,
  `[Register(0)] public float F0;`, declared in each worker for the registers
  its C# uses. Fields, not locals, because the original keeps them across
  waits and entries, and C#'s definite assignment does not follow `goto`.
  A function worker's fields alias its caller's registers (`call` shares
  them).
- **Types:** variables are typed by their location type (`u8` `byte`, `i8`
  `sbyte`, `u16` `ushort`, `i16` `short`, `i32` `int`, `f32` `float`), registers
  `int`/`float`, natives by their signature. ATEL has untyped values and no
  conversions; where C# needs one, the decompiler writes a form that
  compiles to no code: `AsBool(x)` for an `int` used as a condition (the
  bare-value conditions of corpus.md: `if (!AsBool(Foo())) goto l2;`),
  `AsInt(c)` for a comparison used as a value, casts (`V2 = (byte)(V2 + I0);`,
  `(int)` for the 216 float arguments to `int` parameters), `unchecked(…)`
  for a constant out of the target's range (`V1[3] &= unchecked((byte)-65);`)
  and unary `+` where C# warns about `x = x` or `x == x`.
- **Logic:** `land`/`lor` are `&`/`|` on two `bool`s; `and`/`or` the same
  operators on `int`s. The compiler decides by the operand syntax alone: a
  comparison, `!`, `AsBool(…)`, or `&`, `|`, `^` of those is a condition.
  `&&` and `||` are errors (the original never branched inside a
  condition; new code may get them later).
- **Constants:** integers in decimal, from `0x10000` up in hex (motion and
  model IDs); `pushimm` or `pushc` by the `i16` range. Floats with `f`, the
  shortest text that reads back (`R`), negative ones as `-28.2f`.
- **Constant pools are not written** where the corpus.md rules give them
  (`ConstantPools`: integers by first use; floats per statement, non-negative
  first, a negative literal also numbering its absolute value). `[Ints(…)]`
  and `[Floats(…)]` pin the order where the rule fails, or where `Asm` names a
  constant by index (none in the corpus). A pool with a duplicate value
  (`dome00_04`) keeps the statements that use the second copy as `Asm`.
- **2-D arrays:** location bit 24 marks a variable declared as an array, so a
  C# array declaration (also `new byte[1]`) sets it and a scalar does not;
  the table's `+6` word is the row length, so those are `T[,]`
  (`new byte[3, 8]`). The original compiler wrote `a[r][c]` as `c + r * n`,
  folding a constant row: those are `V1[r, c]`; any other flat index is
  `At(V1, i)`. `pushvref` is `Addr(ref V1[i])`.
- **Warnings:** CS0162 (unreachable code: the kept dead code, code after
  `return;`) is suppressed by `ScriptCompiler.Check`, as the SDK will.
  Worker classes are `public` (CS0649 for registers a worker only reads,
  set by its caller).
- Numbers (2026-11-02): all 1,621 scripts come back byte for byte;
  6,073,362 C# statements; **2,006 instructions left in `Asm`** (of 21.3
  million): 1,704 `jmp`s into another entry's method (and the conditional
  jumps that do the same), 38 `nop_76`, the 8 `popv_1`, 6 `endp_1`, 3 `op_39`
  and the statements around them; integer pools pinned in 276 blocks and
  float pools in 462, exactly where the rules fail (908 of 1,184 and 1,113 of
  1,575 follow them), none because of `Asm`; 248.2 MB of source (from 649.5
  MB); 0 C# errors, 0 warnings; the whole corpus decompiles, checks and
  compiles in 75-110 s (with server GC, about 4x faster here; the C# check
  takes most of it: Roslyn's flow analysis over 30,000 labels in one method
  of `bltz0201`, until Phase 4 removes the `goto`s).

### Phase 4: control flow

Decided 2026-11-02, with the structured round trip. What `atel decompile`
writes on top of Phase 3:

- **Structured statements.** `if`/`else` (and `else if`), `while`,
  `while (true)`, `do … while`, `for`, `switch`, `break`, `continue`,
  `goto case`/`goto default`, and `if (false) { … }` (also with `else`) for
  the code the original kept behind a `jmp`. The model and its lowering are
  in `Ffx.Atel.Bytecode.Code` (`Flow.cs`: the records and `FlowEmitter`),
  shared by the compiler and the decompiler's check. The decompiler's
  `Structurer` recognises the shapes the original compiler emitted, one
  method at a time; jumps no construct accounts for stay `goto lN;` /
  `if (…) goto lN;`, with `lN:` labels.
- **The shapes**, as the compiler emits them. `if (c)`: `c; popa_jz else;
  then; [jmp end; else: …;] end:`. `while (c)`: `head: c; popa_jz exit; …;
  jmp head; exit:`; `while (true)` is the same without the test. `do`:
  `head: …; c; popa_jnz head`. `for (init; c; step)`: the original put the
  step before the body, `init; head: c; popa_jz exit; jmp body; step: …; jmp
  head; body: …; jmp step; exit:`, and `continue` jumps to the step.
  `switch`: the selector to B, `jmp chain`, the sections in source order,
  the chain (`push k; pushb; eq; popa_jnz case` per case; the other compares
  are relational patterns, `case < k:`, ranges `case >= lo and <= hi:`; then
  `jmp default` if there is one), and the break label. Where C# can reach
  the end of a section and the original falls through into the next, the
  section ends in `goto case k;` (or `goto default;`) to that next section,
  which compiles to no code; any other `goto case` is a `jmp`.
- **Label numbers** come from a counter per worker that each construct
  draws from as it starts (docs/corpus.md, "Labels"). `if`: the else (or
  end) label at the condition, the end of an `if`/`else` when its else
  starts. `while`, `do`: exit, head. `for`: exit, body, head, step.
  `switch`: chain, break, then each section's label as the section starts
  (after the labels of the sections before it), the range skips with the
  chain. Only labels a jump goes to are numbered; source labels `lN` keep N,
  and the counter skips those numbers. Where the original numbered a
  method's labels otherwise, `[Labels(…)]` on the method lists them in that
  order (a pin; the counter goes on after its highest number).
- **Entries that jump into each other** (the 1,704 `jmp`s of Phase 3 and
  the conditional jumps that do the same; C# has no `goto` between
  methods): the methods from the jumping one to the target become one
  method, and `Entry(n);` marks where each further entry starts (no code).
  Code an entry starts in is not written as `if (false)`; that stays a
  `goto` over it.
- **The last opcodes.** `StoreClamped(ref V3, V3 + 1);` is `popv_1`
  (`popva_1` for an element): store mode 1 clamps the value to the
  variable's range. `End(0, reset: true);` is `endp_1`: it ends, and the
  worker's level starts again at entry 1's code. `RequestParty(level, slot,
  entry);` is `op_39`, a request to a party member's worker. `Nop(1);` is
  `nop_76` with its operand. `Pooled(46, -44.018925f)` is a constant by pool
  index, for a pinned pool that holds the value twice where the code uses
  the later copy (`dome00_04`). `Asm` stays valid input, but the decompiler
  writes none.
- **The safety net.** The decompiler lowers the whole worker with
  `FlowEmitter` and compares it with the original, labels by position. A
  construct whose lowering differs, whose label a remaining `goto` needs, or
  whose block holds a label that a `goto` from outside jumps to (C# cannot
  jump into a block) is dropped, and the method is structured again without
  it (all such constructs of a pass at once). Then the label numbers are
  compared and pinned, and the result is checked byte for byte with the
  pins. A worker that still differed would be written with gotos only; none
  does.
- Numbers (2026-11-02): all 1,621 scripts come back byte for byte; **0
  instructions in `Asm`**; 559,559 constructs: 284,723 `if`, 114,012
  `if`/`else`, 82,531 `switch`, 19,313 `if (false)` and 15,493
  `if (false) … else`, 17,222 `while`, 10,957 `while (true)`, 13,801 `for`,
  1,507 `do`. **8,448 `goto`s left** (6,814 `goto lN;`, 1,634
  `if (…) goto lN;`, of 2.1 million jumps), mostly jumps out of nested
  constructs to a shared end label, or to an outer loop's head, which C#
  has no structured form for. 3,199 constructs dropped by the safety net:
  3,107 because a `goto` from outside jumps into their block, 54 because a
  `goto` needs their label, 38 switches whose last section runs into the
  chain or falls into a range case; none because its lowering differed. **26
  methods pinned with `[Labels]`.** 326 methods hold several entries. 402.8
  MB of source (from 248.2 MB: the indentation of nested blocks). 0 C#
  errors, 0 warnings. The corpus decompiles and compiles in 24 s, 60 s with
  the C# check: Roslyn takes about 36 s of it, half of Phase 3's 72 s on the
  same machine (89 s with the check, 17 s without), as structured code gives
  its flow analysis no methods with tens of thousands of labels.

### Phase 5: typing and naming

Started 2026-11-02. Every name has a source: the reference assembly
(`Ffx.Atel.Game`) holds the names, the decompiler reads them by reflection
(`Ffx.Atel.Decompiler/Naming`), and the compiler resolves `Type.Member` to the
constant (`GameConstants`), so a name compiles to exactly the number it
replaces and the round trip stays byte for byte.

- **Story progress** (`Story`, `src/Ffx.Atel.Game/Story.g.cs`, written by
  `tools/gen-story.cs` from `data/story-progress.json`): one `int` constant per
  value a script stores, 509, named `S` + the value in five digits + the
  scripts that store it, leaving out the developer maps (`test*`, `sample*`)
  unless only they do, and counting `cn_`, `psv_`, `psvcn_` and other prefixed
  copies (`200thunder_kami0400`) as their script: `S01340_Kami0400`,
  `S00055_Bjyt0400_Znkd0500`, `S00562_Bltz0000_Bltz0001_Etc`. Hand names go in
  `data/story-names.json` (`{ "1340": "WakkaJoinsParty" }`). Constants, not an
  enum: `StoryProgress` is a `ushort` in the save, and C# compares and assigns
  `int` constants to it without casts. The decompiler names a constant where
  the story variable (storage 0 `0xa00`) is compared with it, assigned it, or
  switched on (`case >= Story.S02400_Nagi0000 and <= Story.S02875_Zkrn0600:`,
  `goto case`); values no script stores stay numbers.
- **Save variables** (`Save`, `src/Ffx.Atel.Game/Save.cs`): the storage 0
  offset of each save field Fahrenheit names (`SaveData`, as listed in
  ffx-rust's `docs/formats/save.md`) at or after save `+0x1ec`, 65 fields.
  A script's save variable is named after the field at its offset and placed
  by the constant (`[Var(Storage.Save, Save.StoryProgress)] public static
  ushort StoryProgress;`), else `Save_0a4a` by its storage 0 offset; a
  location declared twice gets `_2`. The variable stays a field of the script
  class rather than `Save.StoryProgress` in code: scripts declare the same
  offset with different types and lengths (`0x0000` as a `byte` and as
  `byte[60]`), and the table order is the declaration order. No names are
  known for storage 1 (common variables) yet.
- Numbers (2026-11-02): all 1,621 scripts round-trip; 10,822 story constants
  placed; 760 save variable declarations named after a field, 2,579 by offset.
- **Enums by use** (`src/Ffx.Atel.Game/Enums.g.cs`, written by
  `tools/gen-enums.cs` from Fahrenheit's `src/core/ffx/ids`, the PS2 headers'
  names they transcribed, used with permission; names and values only,
  prefixes dropped, C# case): `Target` (`CHR_*` of `btlmot.ath`, the party
  actors by character from `ply_save.ath`'s `PC_*`, and `Monster0`-`Monster7`
  for actors `0x14`-`0x1b`, ffx-rust battle.md), `ActorProperty` (`STAT_*`
  of `btlhead.src`, the ids `MsGetChrStatInfo`/`MsSetChrStatInfo` switch on),
  `Command` (`PCOM_*`, `ITEM_*`, `MCOM_*`, `MCOM2_*`; members that would start
  with a digit keep their prefix, `Mcom4000`), `BattleMotion` (`MOT_*`).
  Which parameter takes which is hand-maintained in `data/native-types.json`
  (native, parameter name, type and optionally a new name), which
  `tools/gen-natives.cs` applies to `Natives.g.cs` and `data/natives.json`
  (`"type"` on the argument): `Btl.SetActorsProperty(Target actorSelector,
  ActorProperty property, int value)`, `Btl.UseCommand(Target, Command)`,
  27 natives in all. The decompiler writes a constant argument as its member
  (`Btl.SetActorsProperty(Target.Own, ActorProperty.DeathPattern, 3);`), a
  constant without one as a cast (`(BattleMotion)0`), any other value cast
  (`Btl.UseCommand((Target)V2, Command.Mcom409a);`); the compiler drops the
  cast and reads the member as its number.
- **Text**: `Common.MsgSetText(int window, TextId msg)` and
  `Common.MsgGetText_0236(TextId msg)` take a `TextId`, which the worker's
  `Text` indexer gives (`Common.MsgSetText(0, Text[12]);`) and an `int`
  converts to implicitly (a computed index stays as it is). The compiler reads
  `Text[n]` as `n`. No editor extension yet.
- Numbers (2026-11-02): enum members placed 97,654 (`Target` 46,697,
  `ActorProperty` 47,168, `BattleMotion` 2,215, `Command` 1,574), constant
  casts 1,107 (`BattleMotion` 782, `Target` 247, `Command` 73,
  `ActorProperty` 5), computed values cast 8,962; 33,759 `Text[n]`.

## Toolchain

A .NET 10 solution in this repo (Roslyn is MIT):

| Project | Does |
|---|---|
| `Ffx.Atel.Bytecode` | VBF archive reading, script files, disassembler and assembler (our own `ffx-vbf` and `ffx-script` crates from `ffx-rust`, ported); a test checks both agree on all 758 scripts |
| `Ffx.Atel.Game` | the reference assembly: natives, properties, enums, `Save`, `Story`, `Priority`, base classes, `Task`; generated from `ffx-rust`'s native table (`ffx script natives`, to be given a JSON export), Fahrenheit's names and the signature tables |
| `Ffx.Atel.Decompiler` | bytecode to C# syntax trees (Roslyn `SyntaxFactory`), the formatter |
| `Ffx.Atel.Compiler` | a Roslyn compilation of a project to ATEL: lowering, register allocation, layout |
| `Ffx.Atel.Analyzers` | the subset rules, ATL warnings, code fixes |
| `Ffx.Atel.Linker` | applies compiled mod packages to vanilla scripts in load order; a library the loader mod uses |
| `Ffx.Atel.Loader` | the Fahrenheit mod: finds the enabled Atel mods, links them, serves the linked scripts and message tables to the game, provides the `Debug.*` natives |
| `Ffx.Atel.Cli` | `atel decompile`, `atel compile`, `atel link`, `atel roundtrip` |
| `Ffx.Atel.Sdk` | NuGet package: reference assembly, analyzers and MSBuild targets, so `dotnet build` of a mod project writes the mod package |

## Compiler output

- One script file (`.ebp`, monster or battle file) with the rebuilt script
  block (vanilla source, hand edits).
- A mod package: the mod's compiled code and data only, with the vanilla
  points it attaches to (see "Mods").
- A package for runtime injection: the bytecode plus any message additions,
  so a debugger or mod can load it into a running game and extend the
  event's message table in memory (ctrl work `+0x34`).

## Mods

- The modder builds with their own game: `dotnet build` compiles the mod
  against the decompiled vanilla source and writes a package with only the
  mod's code, fields, entries and messages. No package contains game code.
- A mod changes an existing worker with `partial class Wakka { … }` and adds
  new workers, entries, fields and messages by name. The package refers to
  vanilla by script, worker, entry and field, plus a hash of each vanilla
  part it attaches to.
- The loader mod links on the player's machine, with no compiler: it
  checks the hashes against the player's files, applies the enabled
  packages in Fahrenheit's load order, assigns every new number (entries,
  workers, variable offsets, message indices) and gives the game the
  linked files in place of the vanilla ones. Vanilla files are never
  edited; disabling a mod is a relink without it. An Atel mod is a
  Fahrenheit mod that carries packages and depends on the loader.
- Serving (decided 2026-11-02, Phase 2, `docs/research/loader.md`): a
  generated folder. The loader links in its module constructor and writes
  the linked files to its own `efl/x/<archive path>`; Fahrenheit's file
  loader indexes every mod's `efl` folder in its `init`, which runs after
  all constructors, and serves them. The loader owns `efl/x` and empties it
  on each start. Checked in game with an event and a monster script.
- Patches on entries, after Harmony and Mixins: `[Prefix(nameof(OnTalk))]`,
  `[Postfix(…)]`, `[Inject(…)]` at a point inside an entry, `[Replace(…)]`;
  ordering with `[Before("mod")]` / `[After("mod")]`.
- Data edits per field (kernel tables, messages), after RimWorld and the
  Bethesda merge tools.
- `mod.toml`: `source_language`, `after`, `requires`, `incompatible`.
- The loader reports conflicts (log, and in game where Fahrenheit allows): what two
  mods both touch and who wins. Additions and prefix/postfix chains combine
  automatically; replacements and same-field edits are reported conflicts.
  A hash mismatch (another game version, a mod built on edited vanilla) is
  reported too.
- Open: saved data for mods. The save has no known free space; storage 3
  can be redirected through the ctrl work's `+0x54` callback, which nothing
  uses, so the loader might give each mod its own saved variables in a side
  file. Needs research.
- File sizes (answered 2026-11-02, `docs/research/loader.md`): on PC the
  PS2 file layer sizes files with `CDfileSize_PC` (`0x00a428a0`), which
  Fahrenheit hooks so that it reads the replacement's real size from disk.
  A grown event (`kami0000`, +3,776 bytes) and a grown monster (`m209`,
  +576 bytes) loaded whole in game with Fahrenheit alone. `CDfileSize_CD`
  (`0x0076c320`, not hooked by Fahrenheit) is reached only in HDD and
  CD/DVD modes and, on PC, for every `Cd_QueueRead` while an Eternal Calm
  map (393-399) is the graphic map; most of its sizes come from the CD's
  tables. `CDfileSize_CD_EX` (`0x0076c410`, reads `sizetbl.vita.bin`) is
  only called from `CDfileSize_PC`, for modules `0xe`, `0x10`, `0x1a`,
  `0x1c`-`0x22`, `0x28`, `0x29`, on the path Fahrenheit skips
  ([corpus.md](corpus.md#cdfilesize_cd_ex-on-pc)).
  Decision (user, 2026-11-02): the loader hooks `CDfileSize_CD` too, so a
  grown file reports its real size there as well (maps 393-399 only play a
  few specific cutscenes, but it can't hurt). Done the same day
  (`CdFileSizeHook`): for a file in its `efl/x` the hook returns the file's
  size rounded up to 16, everything else goes down Fahrenheit's hook chain.
  It names files the way `Cd_QueueRead` does, with the built-in names of
  the Eternal Calm maps' own `.ebp`/`.mgrp`, whose table entries are empty.
  In game a grown `scene1.ebp` (map 393, reached by a `MapJump` from an
  edited `kami0000`) loaded whole and normal play (map load, battle) was
  unchanged. The game sizes that map's own `.ebp` from disk anyway, so the
  hook changed no result tested; it guards the other files read there
  (models, motion groups, the map).
- Layout of grown code: the assembler moves a worker that outgrows its
  range to the end of the block and leaves the old copy (one added
  instruction in `kami0000` cost 3.7 KB). The compiler lays a changed
  script out afresh from its image instead (`ScriptImage.Write`), so only
  the real growth remains.

## Phases

Each phase ends with a corpus test (`FFX_GAME_DIR`, skipped without it)
and numbers: scripts that round-trip, instructions inside `Asm`, `goto`
count, names placed.

0. **Measure the corpus** (2–3 days). Done 2026-11-02, see
   [corpus.md](corpus.md) and `atel survey`; left for the game: what an
   awaited request to the worker's own entry does at the same or a lower
   priority (`reqe0` at level 1 from entry 1 is the common case, 77
   sites), and whether `UseCommand` checks the monster's command list.
   Stack depth at labels and jumps; jump
   shapes; encoding choices (immediate vs pool, `func` vs `func_facc`,
   store modes, end variants); table and label order against code order;
   ternary and `for` patterns; `default` in switches; worker type meanings
   (types 1-6, and the `battle/btl` scripts); what an awaited request to
   the worker's own entry does at the same or a lower priority (in game);
   fixed entry numbers per type; what each priority is used for; story
   values by the script that sets them; whether `UseCommand` checks the
   monster's command list; near-duplicate workers; whether anything stores
   entry numbers (saves); unreachable code; `CDfileSize_CD_EX` on PC.
1. **Native signatures and the reference assembly** (3–5 days). Argument
   count, kinds, result and polling for every native, from the Rust ports
   and from stack balance, compared; exported as JSON. Generated from it:
   `Ffx.Atel.Game`.
2. **Skeleton round trip** (1–1.5 weeks). The solution, `Ffx.Atel.Bytecode`
   with the cross-check against `ffx-script`, `atel decompile` and `atel
   compile`. Declarations plus every entry as `Asm("""…""")`; every script
   round-trips. Plus a first loader mod: one recompiled script served to the
   game, then one with a visible change, checked in game (done 2026-11-02
   with reassembled disassembly, `scripts/*.asm`; `docs/research/loader.md`).
3. **Expressions and statements** (1 week). Stack code to expressions and
   calls, registers to locals and `[Register]` fields. Done 2026-11-02, see
   "Phase 3: statements": 2,006 instructions left in `Asm`.
4. **Control flow** (1–2 weeks). `if`/`while`/`switch`/`goto case`, label
   order and branch polarity matching the original. Exit: no `Asm` left.
   Done 2026-11-02, see "Phase 4: control flow": no `Asm`, 8,448 `goto`s
   left, 26 label pins.
5. **Typing and naming** (ongoing). Enums by use, properties, text IDs,
   story constants, base classes and engine entries, Fahrenheit's names.
6. **Source layout.** Folders, one file per worker, `partial`, project
   granularity, library base classes for duplicated workers.
7. **The private repo.** `yutamago/ffx-atel-src`; a `generated` branch
   written only by the tool and a `main` branch for hand edits, merged with
   git; the round-trip check runs locally (it needs the game). Commits as
   Yutamago, no user paths.
8. **Tooling.** Analyzers and code fixes, the SDK package, the editor
   extension for message text (VS Code, Rider). No grammar, parser or
   language server of our own.
9. **Mod system.** Package format, `Ffx.Atel.Linker`, the loader's link
   step and file serving, `mod.toml`, patch attributes, per-field data
   edits, the conflict report.
10. **Debug text and runtime packages.** The `Debug.*` natives in a free
    group (in the loader), the injection package.
11. **Documentation.** A language reference (which C# is Atel), the
    encoding rules, a first-mod tutorial.

Phases 0–4: about 5–7 weeks to a fully structured, byte-identical decompile
of every script. 5–11 follow and partly run alongside.

## Out of scope for now

- Running something other than the ATEL VM (running the C# on .NET).
  Atel compiles to ATEL bytecode, which the game's own interpreter runs.
- The Rust port (`ffx-rust`). It runs the same bytecode, so it can load
  the same packages later.
