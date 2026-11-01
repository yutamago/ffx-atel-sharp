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
- The save game is the predefined `Save` class (`Save.StoryProgress`),
  from `ffx-rust`'s `docs/formats/save.md` and Fahrenheit's names; unnamed offsets are
  `Save.Unknown_0123`. Storage 0 starts at `g_SaveData + 0x1ec`
  (`AtelSetUpCtrlWork`), so `Save` offsets are storage 0 offsets plus
  `0x1ec`; story progress is storage 0 `0xa00`.

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
- Story progress: `Story.S1340_WakkaJoinsParty`, the value zero-padded in
  the name so alphabetical order is progress order; auto-named from the
  script that sets each value (`data/story-progress.json`: 432 of the 449
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
  form (1,504 jumps cross into another entry's code). Range and relational
  cases are C# patterns (`case >= 380 and <= 401:`). Dead code the original
  kept is `if (false) { … }`, compiled to a `jmp` over it.
- How the compiler chooses, from Phase 0: `pushc` exactly for values outside
  `i16`; `func_facc` for a call statement, `func` when the result is used;
  `popa_jz` forward, `popa_jnz` back; labels numbered per construct
  (`while`: exit, head; `switch`: chain, break, cases, default). Pinned
  where no rule covers the original: constant-pool and variable-table order
  (pools match a first-use rule in 77 % (ints) and 71 % (floats) of
  blocks), the 8 `popv_1`, label numbers the structurer does not
  reproduce.
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
  linked files in place of the vanilla ones (in memory, or through a
  generated folder, whichever Fahrenheit's file loading supports). Vanilla
  files are never edited; disabling a mod is a relink without it. An Atel
  mod is a Fahrenheit mod that carries packages and depends on the loader.
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
- File sizes: the game sizes files from its own tables (`CDfileSize_CD`
  `0x0076c320` from `cdrom.mdg`, `CDfileSize_PC` `0x00a428a0` from
  `sizetbl.bin`). `CDfileSize_CD_EX` `0x0076c410` reads
  `sizetbl.vita.bin`; on PC `CDfileSize_PC` asks it only for modules `0xe`,
  `0x10`, `0x1a`, `0x1c`-`0x22`, `0x28`, `0x29`, and otherwise takes the
  file's real size ([corpus.md](corpus.md#cdfilesize_cd_ex-on-pc)). A linked
  script that grows needs its new size reported. `ffx-rust`'s hook solved
  this by wrapping the two functions (`crates/ffx-hook/src/mods.rs`); find
  out what Fahrenheit's file replacement already does, and add the same in
  the loader if it doesn't.

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
   game, then one with a visible change, checked in game.
3. **Expressions and statements** (1 week). Stack code to expressions and
   calls, registers to locals and `[Register]` fields.
4. **Control flow** (1–2 weeks). `if`/`while`/`switch`/`goto case`, label
   order and branch polarity matching the original. Exit: no `Asm` left.
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
