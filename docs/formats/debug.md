# Debug text and runtime injection

`Debug.Print`, `Debug.Break` and packages loaded into a running game
(plan: "Text", "Compiler output", Phase 10). Code: `Ffx.Debug`
(`src/Ffx.Atel.Game/Debug.cs`), `DebugCalls` (compiler), `DebugText`
(linker), `DebugNatives`, `Injection` and `RuntimeInjector` (loader).
FFX.exe addresses and names are ffx-rust's (`symbols/ffx.sym`).

## The natives

```csharp
Debug.Print($"hp {Hp,5} speed {Speed:F2}");   // to Fahrenheit's log
Debug.Break();                                // the registers to the log, then an attached .NET debugger
```

The game does not have these: the loader adds them as native group `F`.

| Id | C# | Stack (pushed in this order) | Result |
|---|---|---|---|
| `0xf000` | `Debug.Print(text)` | the values, their count, the text's id | 0 |
| `0xf001` | `Debug.Break()` | nothing | 0 |

Both are int-result handlers only, so a call is done at once: the
interpreter calls the int slot and pushes what it returns
(`Script_FinishNative`). A `Debug.Print` statement is `func_facc 0xf000`.
`Print` pops the id and the count, then that many values, each with the
game's own `AtelPopStackInteger` or `AtelPopStackFloat`, chosen by the
slot's type (1 integer, 2 float), so a hole prints as what the script
pushed. It writes `Atel debug: worker <n>: <text>` to the loader's log.
`Break` writes the worker's registers (`i0`-`i3`, `f0`-`f9`) and the stack
depth, and calls `Debugger.Break()` when a .NET debugger is attached to
the game.

### Why group F

`func id` splits the id into a group (`id >> 12`) and an index; each group
has a table of 16-byte entries (start, poll, float result, int result,
cdecl) in `g_NativeTables` (`0x01328558`). What the game does (disassembly
of FFX.exe):

- `AtelInitTotal` (`0x0086d6d0`, called once, from `TkInit`) first gives
  all 16 groups the same default table, `AtelSetUpCallFunc(g, 0)`, which
  stores `0x00c52b60` for a null table (`0x00877880`), then registers groups
  1, 0, c, 6, 9, 5, 4, b, 7, 8 and d. **Groups 2, 3, a, e and f keep the
  default.** Only `AtelSetUpCallFunc` writes the array.
- The default table has 8 entries, each with start `0x00877710` (a bare
  `ret`) and poll `0x00877720` (returns 0, "not done"), no result handlers;
  after its 128 bytes comes group 1's table (`0x00c52be0`).
- `Script_CallNative` (`0x008777a0`), `Script_PollNative` (`0x00877730`),
  `Script_NativeEntry` (`0x00877770`) and `Script_FinishNative`
  (`0x008777f0`) index the group's table without any check.

So without the loader, a call into an empty group does not crash: with an
index below 8 the worker polls for ever (that level of the worker is
stuck, the rest of the game runs on), with 8 or more it runs whatever
group 1's table holds at that place (`0xf008` would be `0x1000`, a maths
native) and then reads on into other data. That is why the compiler only
emits these calls when asked to, and why the loader fills the whole group:
its table has 4,096 entries (64 KB), every index without a native of its
own going to a handler that logs once and pops nothing.

Atel takes `F`, the last of the free groups; 2, 3, a and e stay free for
other mods. Fahrenheit names group C "Debug"; that is the game's
(`0xc000`-`0xc05d`), not this one.

### Registering

The loader hooks `AtelInitTotal` and, after the original, calls
`AtelSetUpCallFunc(0xf, table)`; it checks that `g_NativeTables[15]` then
holds its table. If the game's tables are already set up when the loader
starts (`g_NativeTables[0]` is not 0), it registers at once as well.

## Strings in bytecode

ATEL has no strings. The compiler pushes a text's **id**, FNV-1a (32-bit)
over its UTF-8 bytes (`DebugText.Id`), and the text travels beside the
code:

- in a mod package, `debugStrings` (a list of the texts its code uses);
- from `atel compile`, `<out>.debug.json` next to the script file.

The loader keeps every text it was given by id (`DebugStringTable`): those
of every linked mod at startup and those of each injected package when it
loads. An id it does not know prints as `#1a2b3c4d [values]`; two texts
with one id (not seen so far) are reported, and the first one wins.

An id is not tied to a place: the block layout does not change it, and a
script needs no table of its own (natives do not know which script a
worker runs). A text is a .NET composite format. The compiler turns
`$"count {Count,3} speed {Speed:F2} {{ok}}"` into
`count {0,3} speed {1:F2} {{ok}}` and pushes `Count`, `Speed`, `2` and the
id; literal text keeps or gets doubled braces (a `$$"""…"""` raw string's
single braces are text). Plain literals and `+` of literals and
interpolated strings are accepted; anything else (a string variable, a
string in a hole) is an error. At most 16 values: the stack holds 20 and
the count and id take two.

## Building without the natives

What `Debug.*` compiles to is a build option (`CompileOptions.Debug`; `atel
compile` and `atel compile-mod`):

| Option | `Debug.*` calls |
|---|---|
| `--debug natives` (default) | the natives above; the script needs the loader |
| `--debug drop`, `--no-debug` | left out, arguments included (as `[Conditional]`), with a warning each |
| `--debug error` | an error each |

A dropped call is still read, so a malformed one is an error in every
mode. The SDK can pass the option from an MSBuild property (`AtelDebug`:
`true`, `false`, `error`; `CompileOptions.ParseDebug` reads those too).
The analyzers allow a string only as an argument of a reference-assembly
member that takes one (ATL1002, [analyzers.md](../analyzers.md#atl1002)),
which `Debug.Print` is. Vanilla scripts have no `Debug` calls, so the
round trip is unchanged.

## Runtime injection

A **runtime injection package is a mod package** (`ScriptPackage`,
[mod-package.md](mod-package.md)), as `atel compile-mod` writes it, with
two optional additions:

- `debugStrings`, as in every package;
- `messages`: `[{ "index": 73, "text": "…" }]`, texts for the script's
  message table by index, in the game's language, written as `atel text
  export` writes them (control codes in braces). An index past the end
  grows the table (the entries between get an empty text); an existing
  index gets the new text and keeps its flags. Only for event scripts:
  their table is ctrl work 0's `+0x34`. A message without an index (from
  `Text.New`) gets the index the linker gives it after every mod's
  ([mod-package.md](mod-package.md#messages)) and is set the same way.

The loader watches its own `inject/` folder (`mods/yuta.atel/inject/`,
every `*.json` under it). At startup the packages there are linked after
every mod in the load order, so their files are served. While the game
runs, a package that appears, changes or goes away is handled on a
background thread, and only the message table is touched on the game
thread (before each frame's update):

| Part | When it takes effect | How |
|---|---|---|
| Debug texts | at once | added to the loader's table |
| Messages | at once, if the event is running; again each time it loads | a copy of the event's table with the messages set, which ctrl work 0 `+0x34` (and `+0x38` where it was the same table) then points at |
| Code | the next time the game loads the script (the map entered again, the next battle) | the file is linked again (the enabled mods, then the package) and the loader's served copy in `efl/x` rewritten |

### What is safe to swap

- **The message table.** `Msg_Text` (`0x0086bf30`) reads ctrl work 0
  `+0x34` on every call (entry `i` at `+i*8`, no bounds check, offset 0
  falls back to the `.ebp`'s own table `g_EventEbpMessages`). The game
  keeps its own pointer to the buffer it read the table into
  (`0x01327128`, from `TKMallocAndReadEventMessage` in `AtelEventSetUp`)
  and frees that one, so a table the loader allocates and puts into
  `+0x34` is never freed by the game, and the game's table stays valid
  for any window still showing its text. The loader frees its own earlier
  tables when another event's table appears, and keeps the latest.
- **Code, only between loads.** A running worker holds absolute pointers
  into its block: the program counter of each level (`AtelWorkThread`'s
  `pc`), the worker header (`+0x00`) and the block (`+0x04`); jumps and
  new entries resolve through the header's label and entry tables, and the
  script's variables live in the block's data areas (storage 2, 3, 4, 6;
  `AtelGetRealVarAdrs`). Putting a new block in place under a running
  worker leaves its levels inside the old code, sends its next jump to the
  new block's label offsets (the same label is somewhere else once code
  changed), and loses the variables' current values unless they are
  copied over at a moment no worker runs. Event workers practically never
  stop running (`Main` loops, waits inside natives), so there is no safe
  moment in general. The loader therefore never changes a script in
  memory: the next load reads the new file.
- **Which files.** Fahrenheit's file loader builds its index of replacement
  files once, at startup, and opens a file by that index when the game
  reads it. Rewriting a file it indexed works; a file that was not served
  at startup cannot be added later. A package for such a file is reported
  and linked from the next start.

A later debugger can do more with the same package: copy the block and
patch only methods no worker is in (by each level's `pc`), or wait for a
map change. Battle message tables (the battle's own, `+0x34` of the battle
ctrl works) are not set yet.

### The language

The messages are encoded with the charset of the language whose files the
game reads, `TOGetFFXLang()` (`0x006410e0`, which `TKMallocAndReadEventMessage`
uses to pick `new_<lang>pc/`): 0 Japanese, 1 English, 2 French, 3 Spanish,
4 German, 5 Italian, 9 Korean, 10 Chinese; another value uses the western
charset. Not `TkGetLang()` (`g_TkLang`), the PS2 setting, which is 0 on PC
with the English files. The loader asks once the first event runs
(`TOGetFFXLang` answers 0 before its localization manager exists).

## Checked in game

See [research/loader.md](../research/loader.md#phase-10-debug-text-and-runtime-injection).
