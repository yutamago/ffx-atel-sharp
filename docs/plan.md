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

2026-11-02, after a review of `ffx-atel-src` from a scenario writer's view
(an FFX fan new to C#):

- **Actors get English names** in the source (`Tidus`, `Wakka`, `Oaka`);
  the `Model` enum keeps the developers' codes (they match the `chr_` files).
- **Hand-written English proper names** (monsters, areas, NPCs) may go into
  this repo; anything extracted in bulk from the game only through a local
  export on the player's machine.
- **Names live in an overlay, applied by the decompiler**, so renames
  survive regeneration (not hand edits on `main`). Changed 2026-11-03: the
  overlay lives in this repo (`data/names/`), not in `ffx-atel-src`. It is
  our own names keyed by worker, entry and variable numbers, with no game
  code or text, and the decompiler then names the same everywhere.
- **Folders: map code plus an English suffix**
  (`Events/Ka_ThunderPlains/Kami0000/`), plus a generated index per area.
- **Two levels of cleanup.** Phase 1 changes keep the bytecode identical and
  go to `main`. Phase 2 cleanups (dead code, dialogue as one call) may change
  the bytecode as long as the game behaves the same; they live on a separate
  branch built by a tool from `main`, which stays byte-identical.
- **Dialogue text as comments on `main`** of the private source repo
  (`Text[71] // "…"`, English).
- **Only verified names.** A name inferred from call patterns (`Op77`, roles
  like `Director`, save-flag meanings) is used only once confirmed in FFX.exe
  or in game.
- **Inline macros for dialogue come after `Text.New`**, which unblocks
  writing new scenarios.
- **The dead `if (false)` blocks stay**, in every form of the source,
  including the Phase 2 branch: they hold code the original compiler kept
  behind constant conditions (34,806 blocks; alternate versions of scenes,
  e.g. a per-character sequence in Macalania Woods, `mcfr0100`, with other
  character entries and `SetLegendAbility` values; dialogue only dead code
  shows). They are the best lead to hidden and cut content.
- **Later: find out what each one does and make it switchable at run
  time.** Each block gets a name (`if (Cut.MacalaniaLegendV1)`) instead of
  `false` in the source, and a mod can turn a block on through a flag the
  loader provides (a native in its group, set from a config or a debug UI),
  so the game runs the hidden variant while vanilla stays as shipped. The
  vanilla source keeps compiling to the shipped bytes: the flags are names
  for `false` until a mod links in the switch.
  Step 1 (static, 2026-11-03): `atel dead` and
  [research/dead-code.md](research/dead-code.md). Most blocks are branches
  of macros whose parameter was a constant (blitzball, the `mcfr0100`
  sequences); a few are real leads, and three are named (`data/cut-names.toml`,
  `Ffx.Cut`).

2026-11-03:

- **Story milestone names keep their number first**, so they still sort by
  story order: `Story.S02385_MacalaniaTempleDone`, not
  `Story.MacalaniaTempleDone`.
- **No NuGet release yet.** The release workflow stays ready (it runs only
  on an `sdk-v*` tag); the user tags a release once they're confident it's
  ready for others to use.
- **CI stays off** on the private repos (no GitHub billing): local
  verification is the merge gate.

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

This section is the design. What the compiler accepts today, and where it
differs from the design (function workers stay classes with `Run()`,
requested entries are static), is in
[language.md](language.md); the bytecode each construct compiles to is in
[encoding.md](encoding.md).

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
- Priorities: `Priority.P0_Main`, `P1` … `P6`, `P7_Init`, `P8`, an enum,
  zero-based, sorted by level (`Ffx.Atel.Priority`; done in Phase 5). Scripts push only
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
  wait, the compiler saves and restores it through hidden storage: the
  private data of the worker that runs the code (storage 3), one copy per
  level (decided 2026-11-02; the shared data is one per script block, which
  two monsters of one kind share in battle, and one entry can run on two
  levels of its worker at once; [encoding.md](encoding.md#locals)).
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
  Done 2026-11-02 for mods (see "Phase 9: mods", `Text.New`): the copy in
  the other languages is the source text, marked untranslated in the
  loader's report (not in the game's text).
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
  folder and one-file-per-worker layout came with Phase 6 (see "Phase 6:
  source layout").
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
  explicit helpers for now; Phase 5 writes most requests in the `await` forms
  of "Requests between workers" (see "Phase 5: typing and naming").
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
  condition; new code may get them later). Since 2026-11-02 new code has
  them, with C#'s short-circuit (encoding.md, "New code").
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
  ffx-rust's `docs/formats/save.md`) at or after save `+0x1ec`, 66 fields,
  and 18 we name ourselves from the scripts (save sphere tips, the Al Bhed
  primers, the lightning dodging awards, the blitzball teams and players),
  with nested classes naming the bits of flag fields (`Save.LightningReward`);
  the evidence, and what stays unnamed, is in
  [research/save-fields.md](research/save-fields.md).
  `[Bits(typeof(Save.LightningReward))]` on a field's constant says which
  class names its bits (2026-11-03), and the decompiler writes a mask of
  `&`, `|` or `^` on that field as the constant with its value
  (`SaveSphereTipsShown[0] |= Save.SaveSphereTip.Level1;`), a byte-wide
  variable at the field's offset only; a mask without a name (the dark aeon
  bytes' lower bits, several bits) stays a number. 1,831 masks named in the
  corpus.
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
  2026-11-03, with our names: 1,801 named after a field, 1,538 by offset.
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
  `Text[n]` as `n`. The editor extension came in Phase 8 ([editor.md](editor.md)).
- Numbers (2026-11-02): enum members placed 97,654 (`Target` 46,697,
  `ActorProperty` 47,168, `BattleMotion` 2,215, `Command` 1,574), constant
  casts 1,107 (`BattleMotion` 782, `Target` 247, `Command` 73,
  `ActorProperty` 5), computed values cast 8,962; 33,759 `Text[n]`.
- **More named values** (2026-11-03, review P7; only verified names):
  `Btl.SetActorProperty` (`0x70ab`, `(property, value)` on the worker's own
  actor: `MsSetChrStatInfo(actor, property, -13, value)`, ffx-rust
  `native_move.rs`) takes an `ActorProperty`. The first argument of 54 more
  battle natives is a `Target`: those whose ported handler picks actors by
  it (ffx.sym `(selector, …)`/`(sel, …)`, `MsGetChrBit`; `FindActor` through
  `MsActTargetSearch(self, selector, property, …)`, its property an
  `ActorProperty`). `Command` for the command of `SetCommandUse`
  (`MsSetCommandUse`), `ChrAddExtraCommand`/`ChrDelExtraCommand`
  (`MsChrAdd/DelExtraCommand(chr, command, selector, …)`) and `QueueCommand_7117`.
  `Target` names the monster ids too: a selector `0x1000 | n` picks every
  actor with that id (`MsGetChrBit`), named after the monster's `chr_` model
  (`Target.Biran`; 315 members, `Monster` in front where `Target` has the
  name). **Battle ids** (`Battle.g.cs`, from btl.h through Fahrenheit's
  `BtlId`, 818): field `<< 16 |` formation, `int` constants as `Story` (the
  scripts keep the id in `int` registers), for `Btl.LabelExe`
  (`MsBattleLabelExe`; 462 of its 493 calls pass one), `EncSetGroupWeight`,
  `CheckSceneField` and the result of `Btl.GetBattleId` (`MsGetBattleScene`):
  `switch (Btl.GetBattleId())` cases and comparisons with it are named. A
  parameter or result takes such constants by `[Values(typeof(Battle))]`
  (`"int:Battle"` in `data/native-types.json`); the decompiler's domains
  now include natives' results. **Maps** (`Map.g.cs`, `tools/gen-maps.cs`):
  the number `MapChange`, `MapChange_00ab`, `MapJump` and `MapChangeDefault`
  take (`AtelSetEventJump2(map, entrance, …)`) is the event's index in the
  game's map table `event/header/eventid.bin` (`MapJump(393, 0)` runs
  `scene1`, checked in game, [loader.md](research/loader.md)); an enum of the
  400 named entries after their scripts (`Map.Hiku0800`), each summary
  giving the script's namespace. Only the event names and numbers are
  written. **Sound effects**: `Sfx`, a `uint` enum without members (bit 31:
  block or separate SE, ffx-rust `sound.md`; no names known), for
  `SfxPlay`, `SfxPlayEx`, `SndSepStop`, `SndSepVolumeDirect`,
  `SndSepVolumeTranslate`, `SepPlaySimple`, so ids read in hex as casts:
  `Common.SfxPlay((Sfx)0x80000017)` (was `SfxPlay(-2147483625)`).
- Not named yet, for lack of a verified source: the message window flags
  (`MsgSetFlags`; the bits are known, `0x400`…`0x8000`, not their meaning),
  pad buttons (the scripts' `g_PadPressed` is remapped by `FUN_00888c40`;
  Fahrenheit's input bits are for another address), field motion ids (per
  model), the motion of `ChrCheckMotion` and other battle natives, the map
  numbers natives return (`GetEventJumpMap`'s cases go past the table),
  battle ids kept in registers before the comparison (`I0 =
  Btl.GetBattleId(); if (I0 == …)`), and sound effect names.
- **Models and workers**: `Model` (`Enums.g.cs`, from `pc.ath`, `mon.ath`,
  `npc.ath`, `sum.ath`, `wep.ath`, `obj.ath`, `skl.ath` through Fahrenheit's
  `*Id` tables; one member per value, the `chr_` name where the header gives
  one, else the file code) types `Common.SetModel` and `Common.ChrLoadAlias`
  (`Common.SetModel(Model.Tidu);`). A worker that is not a function worker and
  loads exactly one model, by a constant, is named after the model's `chr_`
  name (`Takarabox002`, `RikkA`); several workers with the same model get
  their number (`Takarabox002_14`); file codes (`W101`) name nothing, as they
  read like default names; names the reference assembly or the script
  already uses are skipped. Every other worker keeps `W03`. Base classes stay
  `Worker0`-`Worker6`: what the types are (`Npc`, trigger, …) is measured
  from the natives they call but not certain enough to name.
- **Hand names** (decided 2026-11-02, moved 2026-11-03): the overlay in this
  repo's `data/names/<category>/<script>.toml` names workers by number,
  entries by worker and entry number and variables by storage and offset;
  embedded in the decompiler, so renames survive every decompile and every
  decompile names the same (`--names <dir>` uses another folder)
  ([source-repo.md](source-repo.md#names)).
- **English actor names** (decided 2026-11-02): a worker named after an actor
  model whose character is certain gets the English name instead of the code
  (`Tidu` → `Tidus`, `Kima_02` → `Kimahri_02`, `GaOoaka218` → `Oaka`,
  `GaMachen226` → `Maechen`; variants keep their suffix, `TiduH` → `TidusH`);
  the `Model` enum keeps the codes, as they match the `chr_` files. The table
  is hand-written, `data/actor-names.json` (59 models, each with its
  evidence), embedded in the decompiler. Evidence: the party models follow
  `pc.ath`'s order (Tidus 1 … Seymour 8), and the named NPC models
  (`0x20d8`-, `chr_ga_gatta216` on) follow the order of the game's own
  character-name dictionary (`macrodic.dcp` section 6: Gatta, Luzzu, O'aka,
  O'aka XXIII, Dona, …), which the speakers of those workers' messages
  confirm (`GaMachen226`'s workers speak as Maechen 112 times). Models whose
  character is not certain (`Watasi243`, `Deadsummoner244`, `Beruge327`, the
  `*mob*` and crowd models) keep their code. A mod names a worker by its
  class, so a mod written against `Tidu` needs `Tidus` now.
- **Native names and docs** (2026-11-02, review P4/P6): the `docs` section of
  `data/native-types.json` gives the 92 most called natives a modder-facing
  `<summary>` and named, described parameters, written only from what is
  verified (ffx-rust `symbols/ffx.sym` without its `?` guesses, the ported
  natives in `crates/ffx-script/src/vm/bridge/`, the Ghidra dump); the
  symbol's notes move to `<remarks>`. Misleading names went:
  Fahrenheit's `CT_xxxx_ret_name` symbols mean "returns", so `ret` is dropped
  (`RetHasKeyItem` → `HasKeyItem`), and the bare `CT_xxxx_ret` ones got names
  from their handlers: `Chr.Ret` (0x507e) → `Chr.SetAutoTalkOf(worker, on)`
  (`Ch_MouthSetAutoTalk` on that worker's character), `Common.Ret` (0x0060) →
  `WorkerSetFlag200To(on)` (`Atel_SetWorkerFlag200`, then the control
  restored, as 0x0031/0x0032), `Ret_0241` → `SetFieldFlag08`, 0x00d1 →
  `StopMoveAllLevels`, `Btl.RetSetCommandDisabled` → `Btl.SetCommandUse`
  (`MsSetCommandUse` per picked actor), `WaitFrames_01d1` →
  `WaitFramesUnlessHddMode` (its poll is done at once in HDD mode,
  `g_FFXHddMode`). `tools/gen-natives.cs` fails on a `Ret` name and counts
  the names still generated (131: `N0123`, a number suffix, `Cam_605b`);
  those stay until their meaning is verified.
- **`op_77` and `op_78`** (2026-11-02): from ffx-rust's interpreter port
  (`run.rs`) and FFX.exe: `op_77` pops entry and worker; while
  `AtelIsSignalQueued(worker, entry)` (`0x00863570`) finds a request for the
  entry in the worker's queue, the caller's level waits (state 2, rechecked
  every frame). A running request stays queued until its entry ends
  (`AtelExecReturn` frees it), so it waits until every request of that entry
  has run. `op_78` does the same for the worker of a party slot
  (`AtelGetPartyWorker`). The source writes them `WaitForEntry<Lulu>(5);`
  (a constant worker of the script), `WaitForEntry(w, e);` and
  `WaitForPartyEntry(slot, e);`; `Op77`/`Op78` stay valid input. The usual
  shape is `_ = W24.E48Async(); WaitForEntry<W24>(48);`.
- **Engine entries**: a method that holds only entry 0 or only entry 1 of a
  worker (not a function worker) is `protected override void Init()` or
  `Main()`, overriding the base class's virtual members, without `[Entry]`;
  the compiler reads an `Init`/`Main` override without `[Entry]` as entry 0
  or 1. Other entries keep `[Entry(n)] void En()`. A hand-written method
  named `Init` without `override` now gets CS0114 (it hides the base's).
- **Requests as calls** (decided 2026-11-02): an entry another worker of the
  script requests by constants is declared `[Entry(5)] public static async
  Task E5Async()`, and the requests become calls of it: `_ = Lulu.E5Async();`
  (`req0`), `await Lulu.E5Async();` (`reqe0`; `.Finished` reads the same),
  `await Lulu.E5Async().Started;` (`reqw0`). The level is an optional
  `Priority` argument, `Lulu.E5Async(Priority.P3)`, level 1 without it; the
  entry declares `(Priority priority = Priority.P1)` only where some request
  passes one. A method holding an `await` is `async`: `protected override
  async void Main()`, `async void E4()` (the engine runs those, nothing awaits
  them). **Static**, because C# reads `Lulu.E5Async()` as the class `Lulu`
  (the script class's members cannot shadow a type of the namespace), so the
  entry is a static method, and with it the worker's registers
  (`[Register(1)] public static int I1;`, one worker per class, so one copy
  either way) and `RegA`, `RegB`, `LastResult`. The compiler finds the entry
  from the method's first `[Entry(n)]` (or `Init`/`Main`) in the named worker
  class. CS1998 (an `async` method without `await`) is suppressed like CS0162:
  an entry others request is `async Task` whether or not it awaits. The
  fallback form `Request(level, worker, entry)` (and `RequestWaitStart`,
  `RequestWaitEnd`) stays for a computed operand, a request to the worker's own
  entries (the in-game question of Phase 0 is still open), an entry that has no
  method of its own (it shares its offset with a lower entry, or a merged method
  starts it with `Entry(n);`, or it is `Init`/`Main`), the variants (`req2`) and
  `RequestParty`.
- Numbers (2026-11-02): 104,074 of 106,583 requests as calls (77,332
  `_ =`, 23,863 `await`, 2,879 `.Started`); 2,509 fallbacks: 1,504 computed,
  741 to the worker's own entries, 229 to an entry without a method of its own,
  32 `req2`, 3 `RequestParty`. 43,371 methods `async Task`, 1,903 `async void`.
  1,621/1,621 round-trip, 0 C# errors, 0 warnings. A body that requests by
  name names its script's workers, so fewer workers share a library class:
  2,990 workers from 431 classes (3,128 from 492 with the numeric form). A
  library class that requests could name its target's library class, as for
  `Call<…>` under "Not done" of Phase 6.
- The compiler also finds an entry method a worker inherits from its library
  class (`W03.E5Async()` with `E5Async` in `Shared0001`).
- **Function workers** (decided 2026-11-02): a function worker's one entry
  is `public static void Run()` (no `[Entry]`; the compiler reads a static
  `Run` in a type 0 worker as entry 0), and `call` is `W48.Run();`
  (`Call<W48>();` stays valid input). The class stays `public sealed class W48
  : Worker0`, not the plan's `static class`: a static class cannot derive, so
  it would lose the type (`Worker0`), the `Worker` helpers its code uses
  (`AsBool`, `RegA`, …) and the library class the 392 copies of the common
  function share (`W48 : Shared0001 { }`, `W48.Run()` calls the inherited
  method). Named functions (`Common.SetupModels()`) need names, which come
  later. Numbers: all 5,560 `call`s as `Run()`; 1,621/1,621 round-trip, 0 C#
  errors, 0 warnings; library sharing unchanged (2,990 workers, 431 classes).
- **Fewer `AsBool`** (decided 2026-11-02). A constant 0 or 1 inside `!`, `&`
  or `|` of a condition is `false` or `true` (`if (false & V102 < V165[57])`,
  `pushimm 0` … `land`); the compiler reads the literals as 0 and 1. A whole
  condition stays `AsBool(0)`: `if (false)` is dead code and `while (true)`
  has no test. Natives that are predicates (their doc says 1 or 0, a flag, a
  check) and whose result the scripts use only as a condition return `bool`
  (`Common.VoiceIsPlaying`, `Common.PartyIsJoined`, `Btl.CheckRestMotion`, 25
  in all: `"result": "bool"` in `data/native-types.json`, applied by
  `tools/gen-natives.cs`, `"result_type": "bool"` in `data/natives.json`);
  `Common.PadIsPressedRaw` and `Btl.ActorPartnerReady` are used so nearly
  always, and their 5 value uses read `AsInt(…)`. The compiler's test for a
  condition (`land`/`lor` against `and`/`or`) knows those natives. Natives
  only tested for non-zero that return something else (a count, a button
  mask, a side or -1: `PadPressed`, `SelCountField415`, `LineGetCrossSide`,
  11 of them, about 8,000 more `AsBool`) stay `int`, so the type does not
  mislead. No `x != 0` is written: it would add an `ne`.
- Numbers (2026-11-02, without library classes): `AsBool` 88,778 → 40,771
  (32,687 constants now `false`/`true`, about 14,300 bool natives); `AsInt`
  3,344 → 3,349. 1,621/1,621 round-trip, 0 C# errors, 0 warnings. What is left
  is mostly variables and bit tests (`AsBool(V3)`, `AsBool(V3 & 4)`) and
  natives that return values.
- Not done yet: `AsBool`/`AsInt` (40,771 and 3,349 left), register and variable
  names beyond the save, names for function workers, storage 1 names,
  entry names beyond 0 and 1 (requested entries are `E5Async`).
- Numbers (2026-11-02): 13,248 `Model` members (21 casts); 3,684 of 15,010
  workers named after their model; 29,136 `Init`/`Main` overrides. The
  corpus still round-trips 1,621/1,621 with 0 C# errors and 0 warnings.

### Phase 6: source layout

Decided 2026-11-02. What `atel decompile -o` writes on top of Phase 4, and
how `atel compile` reads it:

- **A script is a folder and a namespace**, `Events/Bj/Bjyt0400/` (the
  namespace as folders, as in Phase 2): `Bjyt0400.cs` holds the script
  class (`[Event]`, `[Workers]`, `[Header]`, the pins and pools, the
  variables), each worker has its own file named after its class
  (`W03.cs`, with `[Worker]` and `[PrivateData]`), and the data areas are a
  second part of the script class in `Bjyt0400.Data.cs`. Every file starts
  with the same comment, usings and namespace.
- **Big workers are `partial`.** A worker of more than 4,000 lines is cut
  between members into parts of at least 2,000 lines: `W36.cs` (attributes,
  base class, registers and the first methods), `W36.2.cs`, … (`W36.02.cs`
  from 10 parts). One method is never cut, so the largest file is still a
  single method (`bltz0000`'s `W36`, 191k lines, 15.7 MB).
- **Order.** The workers' order is `[Workers]`'s alone. A worker's methods
  are laid out in the order of its parts, then in declaration order; the
  parts' order is the order of their paths without `.cs`, ordinal
  (`SourceOrder`: `W36` before `W36.2`), whatever order the files are
  given in.
- **The compiler reads a folder**: every `.cs` under it except in `obj/`
  and `bin/` (`ScriptCompiler.SourceFiles`); `atel compile <folder>`.
- **Projects** (measured 2026-11-02, `dotnet build` of a generated
  `.csproj` against `Ffx.Atel.Game`, in-process compiler, one machine with
  16 threads; times include about 3 s of MSBuild):

  | One project holding | Source on disk | Build | Peak memory |
  |---|---|---|---|
  | one script (`bjyt0400`) | 2 MB | 6.7 s | 0.2 GB |
  | an event area (`Events/Bj`, `Events/Lc`) | 2-11 MB | 3.9-7.4 s | 0.25-0.9 GB |
  | all monsters | 10 MB | 5.4 s | 0.5 GB |
  | a battle area (`Battles/Kino`, `Battles/Zzzz`) | 6-25 MB | 3.9-9.3 s | 0.5-1.6 GB |
  | one blitzball script (`bltz0000`) | 38 MB | 30-50 s | 2.1-2.3 GB |
  | all battles | 78 MB | 42.5 s | 4.6 GB |
  | all events but blitzball | 96 MB | 57.5 s | 5.3 GB |
  | the blitzball area (`Events/Bl`) | 258 MB | 174 s | 13.6 GB |
  | all events | 354 MB | 258 s | 17.4 GB |
  | the whole corpus | 441 MB | 160 s | 22.0 GB |

  An IDE holds about what the compiler does, so the corpus as one project
  is too much, and so is any category. **A project is a map area**:
  `Events/Bj/Events.Bj.csproj` (48 event areas), `Battles/Kino/…` (battle
  scripts by the letters their name starts with, their map area; the
  namespace became `Battles.Kino.Kino00_00`; since 2026-11-02 the folders
  carry the area's English name, see "Area folders"), all monsters in
  `Monsters/Monsters.csproj`; an area of more than 64 million characters
  (only blitzball) is a project per script. 104 projects, each found by the
  SDK's default glob; the largest are the blitzball scripts (about 2 GB).
- **Root files** from `atel decompile -o`: `Atel.slnx` with every project
  (builds with `dotnet build`, 0 errors, 0 warnings, about 4 minutes in
  parallel), `Events.slnx`, `Battles.slnx` and `Monsters.slnx` to open one
  category, `.gitignore`, and `Directory.Build.props` with the shared
  settings (net10.0, no implicit usings, CS0162 off) and the reference to
  `Ffx.Atel.Game`: `$(AtelGameAssembly)`, by default the copy `atel
  decompile` puts in `.atel/Ffx.Atel.Game.dll` (with its XML docs), or,
  with `-p:AtelGameProject=<path to Ffx.Atel.Game.csproj>`, a project
  reference to a checkout of this repo. Since Phase 8 the projects use the
  SDK package instead (`--no-sdk` keeps this way; [sdk.md](sdk.md)). The
  compiler ignores `obj/` and `bin/` in a script's folder.
- **Area folders** (decided 2026-11-02): an area folder is the map code plus
  the area's English name, `Events/Ka_ThunderPlains/Kami0000/`,
  `Battles/Kami_ThunderPlains/Kami00_00/`, from the hand-written
  `data/areas.json` (36 event folders, 27 battle areas; the developer maps,
  `Zzzz`, `Tori`, `System`, the ending and the opening keep the bare code).
  Each name is the place the game's own location names give for the area:
  an event's save location is its script block's `u16 +0x1e` (or, with bit
  15, an `i16` table at `+0x28`; `AtelSetSaveDicIndex`), an index into
  section 11 of `menu/macrodic.dcp` (`kami0000` → "Thunder Plains - South").
  **Namespaces follow the folders** (`Events.Ka_ThunderPlains.Kami0000`):
  the SDK and `atel compile-mod` find a mod's vanilla script by its
  namespace as folders, so the two must agree. Renaming costs nothing in the
  bytes, but a mod names its script by namespace: a mod written against
  `Events.Ka.Kami0000` needs `Events.Ka_ThunderPlains.Kami0000` now (monsters
  are unchanged). `AREAS.md` at the root (written by `atel decompile -o`)
  lists per folder the scripts with their place names, named workers and
  notes, and per battle the monsters of its formation (section 2 `+0x0c`).
- **Scripts the PC build does not load** (2026-11-02, from FFX.exe): its map
  table `event/header/eventid.bin` names 376 of the 397 events; the 21
  others are the `cn_`, `psv_` and `psvcn_` copies (no string in FFX.exe
  names those prefixes: other builds' files), `testpub0`, and three that the
  file-name code (`0x00a42c00`, `read_debug.c`) loads in place of the
  original only behind debug-menu switches: `200thunder_kami0400`,
  `full_nagi0700`, `dbg_nagi0000`. Each of their files says so at the top
  (`data/areas.json`, "scripts"), as does `AREAS.md`. They stay in their
  folders, so their namespaces do not change.
- **Solutions** (2026-11-02): the single-script projects (blitzball, about
  2 GB each; `W36` of `bltz0000` is one 15.7 MB method) are only in
  `Blitzball.slnx`, not in `Atel.slnx` or `Events.slnx`; `Start.slnx` holds
  the Thunder Plains (events and battles), the monsters and the library, and
  `.vscode/` recommends C# Dev Kit and opens `Start.slnx`. The layout pins
  (`[Header]`, `[Pin]`, `[Patch]`, the pools) moved from the main file to
  the data part (`Kami0000.Data.cs`), so the main file reads as the script's
  outline: its workers and variables. (Changed 2026-11-03: a layout part of
  their own, see "Layout and data parts".)
- **Library base classes** for duplicated workers ("Shared behaviour"):
  a worker whose type and body text equal another's, anywhere in the
  corpus, becomes `public sealed class W03 : Shared0001 { }` (with
  `using Library;`), and the body moves to `Library/Shared0001.cs`,
  `public abstract class Shared0001 : Worker1`, with a comment listing its
  users. `[Worker]` and `[PrivateData]` stay on each worker. The compiler
  takes the type from the library class and lays its members out before
  the worker's own (`ScriptLibrary`; `atel compile` finds the nearest
  `Library/` above the script folder, or `--library <dir>`); the library
  is a project every other project references. Names are neutral
  (`Shared0001`, numbered by first use in path order) until a name is
  certain.
- **What makes two workers copies at the source level:** the same base and
  the same body text, and the body names nothing its script declares (the
  script class, its variables, its workers). The library must compile on
  its own, and everything else a body names means the same in every
  script: natives, registers, constants by value (the pools follow from
  the code, or are pinned and hold every value), request and party
  numbers, labels (pinned per method). So equal text gives equal bytes;
  the round trip checks it.
- Numbers (2026-11-02): 977 groups of equal workers (3,425 copies beyond
  the first, 39.1 MB); **492 library classes, from which 3,128 workers
  derive** (2,636 copies removed), saving **1.9 MB**; 486 of the classes
  span several scripts. The rest name script variables or workers: the big
  copies are the blitzball variants (`bltz0000`, `cn_bltz0000`, …), whose
  workers use their scripts' variables, so a library class could not
  compile on its own. 1,621 scripts round-trip byte for byte, 0 C# errors,
  0 warnings, with the library: 403.8 MB in 19,267 files.
- **Library names** (2026-11-03, review proposal P15): the numbers shift
  between exports, so a hand name is keyed by the class's **signature**, a
  hash of its workers' code (worker type, entry and label offsets, every
  instruction with constants by value and variables by location;
  `WorkerSignature`), which neither the script nor the decompiler's C#
  changes. `data/library-names.toml` maps signatures to names and a summary
  (the class's doc comment); unnamed classes keep `SharedNNNN`, numbered as
  before (named classes still take their number, so naming one renumbers
  nothing). Two classes of the same code (written `async` in one script,
  plain in another) share a signature: the one with more users gets the
  name, the other `Name_2`. The names say what the code does, read off the
  natives it calls (`EventCamera`: `AttachCamera(1, 0, 0)`;
  `ThunderPlainsBattleLights`; `IdleWorker2`), not a role. Numbers: 16 of
  434 classes named, used by 1,083 of the 3,006 workers that derive from
  the library; all users of every class share one signature (433 distinct
  signatures for 434 classes). See [source-repo.md](source-repo.md),
  "Library names".
- **Notes read off the code** (2026-11-03, review proposal P10), comments
  only: each entry method that other workers start gets `/// <summary>Requested
  by W57.Main, Tidus.E20Async.</summary>` (or `Called by …` for a function
  worker), and the script class the **story index**: every `switch
  (StoryProgress)` in a worker's `Init` or `Main` that requests entries, with
  the entries each case requests. On the script class, not a `Scenes.md` per
  area: it travels with the script (`atel decompile kami0000` has it too),
  shows on hover in the IDE, and the requests it names are a search away in
  the same folder; a per-area file would duplicate `AREAS.md`'s role and go
  stale against hand edits. The notes stay out of the body text, so library
  sharing is unchanged (a shared worker's notes go on its class line).
  Numbers: 42,986 note lines on methods, 1,372 on shared workers' classes;
  story indexes on 124 event scripts. 1,621/1,621 scripts compile to the
  game's bytes (`atel compile-all --check`), 0 C# errors, the 221 ATL2006
  warnings of vanilla.
- Not done: bodies that call a sibling worker (`Call<W48>()`, 66 groups,
  75 more workers) could name the library class of the target
  (`Call<Shared0002>()`, resolved to the script's worker deriving from it);
  near-duplicates with parameters.
- **Layout and data parts** (2026-11-03, review proposal P16): what only
  pins the compiled layout is in `<Script>.Layout.cs`, the data areas in
  `<Script>.Data.cs`, and neither in the files people read: the layout part
  holds `[Header]`, `[Pin]`, `[Patch]` and the pools on a part of the script
  class, and each worker's `[Worker(PrivateLength = …)]` on a part of the
  worker (`partial class W39 { }`); the data part the data areas and each
  worker's `[PrivateData(…)]` the same way. The compiler already read a
  class's attributes from all its parts; worker files now hold the class
  line, registers and code only. `atel decompile <script>` to the console
  (one file) keeps the attributes on the worker. Numbers: 3,178
  `[Worker]` and 2,193 `[PrivateData]` moved off 15,010 worker files;
  20,784 files (1,621 layout parts, 1,621 data parts).
- **Variants** (2026-11-03, review proposals P17, P22): the scripts the PC
  build does not load or loads only behind a debug switch (the 21 with a
  note in `data/areas.json`) and the developers' test maps and battles
  (names with `test`, `sample*`: `Events/Te`, `Ca`, `So`, `Sa`,
  `Battles/Test`) are variants (`ProjectLayout.IsVariant`). The least
  disruptive way to keep them out of the way: **projects, not folders**.
  A `Variants/` folder would change their namespaces (a mod names its
  script by namespace) and the paths `compile-all` and hand edits on `main`
  use; a solution filter (`.slnf`) would hide them in one IDE view only,
  while `dotnet build Atel.slnx` still compiled them (also the blitzball
  copies). So the scripts stay where they are;
  an area of variants only is one project as before (`Events/Te/`), and a
  variant in an area of loaded scripts gets a project inside the area's
  folder (`Events/Ka_ThunderPlains/_200thunder_kami0400/`), which the
  area's project leaves out (`DefaultItemExcludes`). Only `Variants.slnx`
  holds their projects: `Atel.slnx`, `Events.slnx`, `Battles.slnx` and
  `Start.slnx` show what the game plays. The blitzball copies (`cn_`,
  `psv_`, `psvcn_` of `bltz0000` … `bltz0201`, with `Cn_bltz0000/W36.cs`
  among the 15.7 MB methods) move from `Blitzball.slnx` to `Variants.slnx`;
  `Blitzball.slnx` keeps the 9 loaded blitzball scripts (`Bltz0000` and
  `Bltz0002` have their own 15.7 MB `W36`), which stay out of the default
  solutions as before. Numbers: 109 projects (104 before: 5 variants got
  their own, in `hi`, `ka`, `lc` and `na`), `Atel.slnx` 75 and the library,
  `Variants.slnx` 25, `Blitzball.slnx` 9.
- Numbers (2026-11-02): all 1,621 scripts come back byte for byte from
  18,778 files (1,621 script classes, 1,621 data files, 15,010 workers, 526
  further parts of 160 workers), 405.5 MB; 0 C# errors, 0 warnings.

## Toolchain

A .NET 10 solution in this repo (Roslyn is MIT):

| Project | Does |
|---|---|
| `Ffx.Atel.Bytecode` | VBF archive reading, script files, disassembler and assembler (our own `ffx-vbf` and `ffx-script` crates from `ffx-rust`, ported); a test checks both agree on all 758 scripts |
| `Ffx.Atel.Game` | the reference assembly: natives, properties, enums, `Save`, `Story`, `Priority`, base classes, `Task`; generated from `ffx-rust`'s native table (`ffx script natives`, to be given a JSON export), Fahrenheit's names and the signature tables |
| `Ffx.Atel.Decompiler` | bytecode to C# source text (written as text, not with Roslyn's `SyntaxFactory`; see "Phase 2: the source shape"), the source layout, library classes |
| `Ffx.Atel.Compiler` | a Roslyn compilation of a project to ATEL: lowering, register allocation, layout |
| `Ffx.Atel.Analyzers` | the subset rules, ATL warnings, code fixes |
| `Ffx.Atel.Linker` | applies compiled mod packages to vanilla scripts in load order; a library the loader mod uses |
| `Ffx.Atel.Loader` | the Fahrenheit mod: finds the enabled Atel mods, links them, serves the linked scripts and message tables to the game, provides the `Debug.*` natives |
| `Ffx.Atel.Cli` | `atel decompile`, `atel compile`, `atel compile-all`, `atel roundtrip`, `atel export`, `atel text export`, `atel compile-mod`, `atel link`, `atel survey`, `atel disasm`/`asm` |
| `Ffx.Atel.Sdk` | NuGet package, an MSBuild project SDK: reference assembly, analyzers and MSBuild targets, so `dotnet build` of a mod project writes the mod package ([sdk.md](sdk.md)) |

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
  the real growth remains. Caution (found 2026-11-02, Phase 9): the data
  areas hold block offsets. The "other" area points into the shared data
  in 1,611 of 1,621 scripts (`+0x0c` is the shared data's offset in
  1,316), and the shared data points into itself in 1,545. A layout that
  moves the shared data when the code grows leaves those pointing at the
  old place; whether the game reads them is not known. The mod linker
  therefore keeps every vanilla byte in place (see "Phase 9: mods").

### Phase 9: mods

Started 2026-11-02. Format: [formats/mod-package.md](formats/mod-package.md).

- **A package per script** (`atel/<archive path>.json` in the mod's
  folder, JSON): the mod's new variables, new workers, new entries and
  patches on vanilla entries (`prefix`, `postfix`, `replace`), as code
  fragments whose operands the linker fills in (labels, pool constants,
  variables, worker and entry numbers). Vanilla parts are named by number
  and name with a hash (SHA-256 of the worker, of the patched method's
  code range, of the variable's table entry); no game code is in it.
- **`Ffx.Atel.Linker`** (no Roslyn; runs in the loader): checks the
  hashes (a mismatch leaves that mod's package for that script out, with
  an error in the report), numbers everything new in load order, chains
  all mods' patches on one vanilla method (prefixes in load order, the
  body, postfixes in load order; two replacements are a conflict and the
  last wins), and writes the block without moving any vanilla byte: grown
  tables, new headers, new variables' data and the whole code region go
  after the block's end. `mod.toml` (`requires`, `incompatible`, `after`)
  is read by `ModSet`.
- Numbers (2026-11-02): a package with a prefix, a postfix, a new entry
  that requests itself and a new variable links onto 1,598 scripts (the
  other 23 have no workers or no shared data); each result parses, takes
  itself apart and lays itself out again byte for byte, and keeps its data
  areas, the other workers' code and the file's other sections. Files grow
  by 36 KB on average, as the code region moves; laying the code out in two
  runs is left for later.
- **`atel compile-mod <mod dir> --vanilla <dir> -o <out> [--id <id>]`**
  (`ScriptCompiler.CompileMod`): a mod is C# in the namespace of the script
  it changes, next to the decompiled vanilla source (`atel decompile -o`).
  `partial class M209 { public static int Turns; }` adds variables (no
  `[Var]`: the linker allocates them in the shared data); `partial class
  W01 { … }` adds to a vanilla worker: a method without an attribute is a
  new entry (an `async` one since helpers came, a plain one being a helper), `[Prefix(nameof(Init))]`, `[Postfix(…)]` and `[Replace(…)]`
  patch a vanilla method (`Ffx.Atel.Game/Atel/ModAttributes.cs`); a new
  class deriving from `Worker0` … `Worker6` is a new worker.
  A mod requests its new entries as any entry (`_ = W01.BoostAsync();`);
  `Link.Worker<T>()` and `Link.Entry<T>(nameof(…))` give the numbers for
  the explicit `Request(level, worker, entry)` form. Where the linker gives
  the number, the code model has a `SymbolExpr`. The
  compiler compiles the vanilla source, reads the mod's members against its
  declarations, and keeps only the mod's code, as fragments. The decompiler
  now writes every worker `partial`, so the mod and the vanilla source
  compile together as C# (`--check`).
- **`atel link <mod dir>… -o <dir>`** links mod folders onto the game's
  files as the loader does and prints the report.
- Numbers (2026-11-02): a postfix on the Larva's (`m209`) worker 1 `Init`
  that sets its HP is a 9-byte package; a mod that adds a postfix which
  requests a new entry links onto `m209`, and the linked file decompiles
  to C# without errors and compiles back byte for byte, with workers 0 and
  2 unchanged.
- **The loader links** (`ModLoader`, in the module constructor as before):
  it reads Fahrenheit's `mods/loadorder` itself (Fahrenheit publishes its
  mod list only after the constructors), takes every listed mod with an
  `atel/` folder as an Atel mod (a file-only Fahrenheit mod: manifest,
  `mod.toml`, packages; `compile-mod --id` writes the manifest), resolves
  `requires`/`incompatible`/`after`, links per file and writes the results
  to its `efl/x`. The report goes to Fahrenheit's log (errors as errors,
  conflicts and warnings as warnings). The Phase 2 `scripts/*.asm` path
  stays for development and wins over mods for the same file (reported as
  a conflict).
- Checked in game (2026-11-02, [research/loader.md](research/loader.md#phase-9-a-linked-mod-package-in-game)):
  the Larva HP postfix as a mod package (`atel compile-mod`, a file-only
  Fahrenheit mod after `yuta.atel` in the load order), linked by the loader
  to a 56,624-byte `m209.bin` (vanilla 55,280). In battle 34 the Larva has
  1234 HP, and its script block in memory equals the linked block byte for
  byte, including the code region the linker moved to the block's end
  (`+0x30` = `0xa70`) and everything past the vanilla block. Open: laying
  out unchanged workers in place; per-field data edits and `[Inject]`;
  private data for new workers.
- **Hidden locals in private data** (2026-11-02). Hidden locals used to be
  4-byte variables of the shared data (storage 4). FFX.exe
  ([formats/script-block.md](formats/script-block.md#private-data-at-run-time)):
  storage 3 goes through the running worker's work (header `+0x2c`, in
  battle the ctrl work's `+0x54` callback to the work's own buffer at
  `+0xb8`, sized by `+0x10`, cleared at set-up); `call` keeps the caller's
  work; two monsters of one kind share one script block (one shared data)
  and get a set of works each. So a hidden local is now an array of nine in
  storage 3, indexed by the level the worker runs (native `0x00eb`, work
  `+0x32`): `func 0x00eb; pushva v` to read, `func 0x00eb; …; popva v` to
  write. An entry's parameter and result fields are one plain variable
  each, private when only the entry's own worker requests it, else still
  shared (storage 3 is the running worker's, so another worker could not
  write the entry's copy). The linker allocates new storage-3 variables in
  one area that every worker running the code (its own or a function
  worker's it calls) gets past its private data, copying a vanilla private
  block to the block's end when it grows ([formats/mod-package.md](formats/mod-package.md#linking));
  a script compiled whole grows those workers' private data. The linker's
  corpus package now has such a local: 1,598 scripts link and lay
  themselves out again byte for byte; the vanilla round trip is unchanged
  (no new code). In game, the two Larvas of `kami03_02` each kept their own
  hidden local ([research/loader.md](research/loader.md#hidden-locals-in-private-data-two-larvas)).
- **`Text.New`** (2026-11-02, [formats/mod-package.md](formats/mod-package.md#messages)):
  `Text.New("Nice throw!")` in a mod is a `TextId` of the reference
  assembly; the compiler puts each text once into the package's `messages`
  without an index and pushes it through a `newMessage` relocation. The
  linker (`ModSet.Link`, `MessageLinker`) numbers the mods' new messages in
  load order from one past the longest of the script's tables (every
  language's and the `.ebp`'s own), and gives every language that has a
  table a copy grown to the new indices, each message encoded in that
  language's charset: the mod's `source_language` the text, the others a
  copy reported as untranslated. The loader serves the tables from its
  `efl/x` like the scripts; `atel link` writes them too. A monster (no
  table of its own) and a script compiled whole reject `Text.New`. A
  runtime injection package's new messages are numbered after every mod's
  and set in the running event's table.

- **Inline methods; dialogue as one call** (2026-11-03, Phase 1: byte-identical;
  [language.md](language.md#inline-methods), [encoding.md](encoding.md#inline-methods)).
  `[Inline]` methods expand at the call site: the compiler reads the body
  once into a template (statements with a placeholder per parameter) and
  copies it with the arguments substituted, so labels and pools come out as
  the written-out code's. `Ffx.Dialogue` in the reference assembly (source
  embedded for the compiler): `Open`, `WaitUntilOpen`, `WaitUntilReady`,
  `Say`, `SayVoiced`, `SayVoicedToEnd`, chosen from a survey of the
  decompiled tree (33,038 `MsgOpen`; the five-call opening is in every
  line, the tails vary: unvoiced `MsgIsReady` loop 27%, voiced with skip
  15%, open only 15% (blitzball, choices), voiced to the end 5%, …). The
  talking motion (`Chr.Ret`) and voice preparation stay their own calls.
  The decompiler folds runs that unify with a template and keeps them only
  if the worker still checks: 30,513 of 33,038 lines are one call (2,306
  are in dead `if (false)` code and stay as they are; 219 others), 0
  fallbacks; the source is 394.6 MB instead of 408.7 MB (7.75 instead of
  8.10 million lines); the round trip is 1,621/1,621. Analyzers: ATL3003
  (an inline method calls itself), ATL3004 (an argument with an effect);
  an inline call is no `call` level and `[Inline(Waits = true)]` is a wait.

### Phase 10: debug text and runtime packages

Done 2026-11-02. Format and design: [formats/debug.md](formats/debug.md);
in game: [research/loader.md](research/loader.md#phase-10-debug-text-and-runtime-injection).

- **Group F.** `AtelInitTotal` leaves native groups 2, 3, a, e and f with
  the game's default table (8 entries that never finish; no check of the
  group or index anywhere), so a call into one without the loader parks
  that level of the worker for ever. The loader takes F: it hooks
  `AtelInitTotal` and registers a 4,096-entry table after it.
  `Debug.Print` is `0xf000`, `Debug.Break` `0xf001`.
- **Strings.** The compiler pushes a text's id (FNV-1a of its UTF-8), the
  package carries the text (`debugStrings`), the loader keeps all texts by
  id. An interpolated string is a composite format whose values (`int`,
  `float`, `bool`; at most 16) are pushed before their count and the id;
  the native prints each by its stack slot's type.
- **Without the loader** (decided here, as the plan asked: "refuses it or
  drops it on request"): `--debug natives` (default), `--debug drop`
  (`--no-debug`; each call left out with a warning) or `--debug error`;
  `CompileOptions.Debug` in the compiler, for the SDK's `AtelDebug`
  property. The vanilla round trip is unchanged (no `Debug` calls).
- **Runtime injection.** A mod package (plus `messages`) in the loader's
  `inject/` folder. Code takes effect at the script's next load (the file
  is linked again and served), messages at once in the running event's
  table (ctrl work 0 `+0x34`), debug texts at once. A running script is
  never changed in memory: its workers hold absolute pointers into the
  block, and event workers never stop.
- Also fixed: `atel compile-mod` failed on a script with a function worker
  (`Run()` was not read as entry 0 in the vanilla method map).
- Open: an on-screen line for `Debug.Print`; battle message tables (in
  memory; `Text.New` adds to the battle's table files when linking); in-memory code swaps for a debugger
  (copy the block, patch only methods no worker is in).

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
   granularity, library base classes for duplicated workers. Done
   2026-11-02, see "Phase 6: source layout": 18,778 files in 104 projects,
   3,128 workers derived from 492 library classes.
7. **The private repo.** `yutamago/ffx-atel-src`; a `generated` branch
   written only by the tool and a `main` branch for hand edits, merged with
   git; the round-trip check runs locally (it needs the game). Commits as
   Yutamago, no user paths.
   Done 2026-11-02, see [source-repo.md](source-repo.md): `atel export`
   (decompile onto `generated`, merge into `main`, check) and `atel
   compile-all` (every script folder of a tree against the game's files).
8. **Tooling.** Analyzers and code fixes, the SDK package, the editor
   extension for message text (VS Code, Rider). No grammar, parser or
   language server of our own. Analyzers done 2026-11-02
   ([analyzers.md](analyzers.md)): 21 rules, run by `atel compile --check`;
   on the vanilla source no errors, ATL0001 136 times. Message text done
   2026-11-02 ([editor.md](editor.md)): `atel text export` (the player's
   tables, decoded, as `.atel/text/<lang>/<script>.json`, never committed),
   the VS Code extension (`editors/vscode`: inlay hint and hover, language
   setting) and `atel decompile --text-comments <lang>`; all 33,759
   `Text[n]` are in event scripts. Rider plugin done 2026-11-02
   (`editors/rider`, a ReSharper backend plugin): inlay hint and quick
   documentation on `Text[n]`, a settings page; built against Rider 263.*,
   tested headless in CI, not published ([editor.md](editor.md)).
   SDK done 2026-11-02 ([sdk.md](sdk.md)): `Ffx.Atel.Sdk` is an MSBuild
   project SDK (`<Project Sdk="Ffx.Atel.Sdk/0.1.0-alpha">`) holding
   `Ffx.Atel.Game` with its docs, the analyzers and code fixes, Atel's
   settings and the `atel` CLI (framework-dependent, 4.4 MB packed). In a mod
   project, `dotnet build` adds the vanilla source of the scripts the mod's
   namespaces name (`AtelVanillaSource`) to the C# compilation, as generated
   code, so the analyzers see only the mod. It then runs `atel compile-mod`
   (needs `FFX_GAME_DIR`) and writes `bin/…/mods/<id>/`: the manifest
   (depending on `yuta.atel`), `mod.toml` and `atel/<archive path>.json`. The
   DLL holds vanilla code and is not copied. `atel decompile -o` writes
   SDK projects (`global.json` pins the version; the package goes to
   `.atel/packages/` with a `nuget.config` when found or given with `--sdk`),
   `--no-sdk` the old `.atel/Ffx.Atel.Game.dll` reference. Example:
   `examples/LarvaHp`; `tests/Ffx.Atel.Sdk.Tests` packs the SDK, decompiles
   m209, builds the vanilla project and the example, and gets the same
   package as `atel compile-mod`. CI packs it on all three OSes; it is not
   published anywhere yet. Open: publishing the package, a `dotnet new`
   template.
9. **Mod system.** Package format, `Ffx.Atel.Linker`, the loader's link
   step and file serving, `mod.toml`, patch attributes, per-field data
   edits, the conflict report. Started 2026-11-02, see "Phase 9: mods":
   packages, the linker, `atel compile-mod`, `atel link` and the loader's
   link step are done and checked in game, `Text.New` too; per-field data
   edits and `[Inject]` are open.
10. **Debug text and runtime packages.** The `Debug.*` natives in a free
    group (in the loader), the injection package. Done 2026-11-02, see
    "Phase 10: debug text and runtime packages".
11. **Documentation.** A language reference (which C# is Atel), the
    encoding rules, a first-mod tutorial. Done 2026-11-02:
    [language.md](language.md), [encoding.md](encoding.md),
    [tutorial.md](tutorial.md). The tutorial builds the mod with the SDK
    (`dotnet build` of an `Ffx.Atel.Sdk` project) since 2026-11-02, with
    `atel compile-mod` as the way without it; its steps were run up to the
    linked file.

Phases 0–4: about 5–7 weeks to a fully structured, byte-identical decompile
of every script. 5–11 follow and partly run alongside.

## Out of scope for now

- Running something other than the ATEL VM (running the C# on .NET).
  Atel compiles to ATEL bytecode, which the game's own interpreter runs.
- The Rust port (`ffx-rust`). It runs the same bytecode, so it can load
  the same packages later.
