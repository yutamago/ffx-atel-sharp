# Debug text and runtime injection

`Debug.Print`, `Debug.Break` and packages loaded into a running game
(plan: "Text", "Compiler output", Phase 10). Code: `Ffx.Debug`
(`src/Ffx.Atel.Game/Debug.cs`), `DebugCalls` (compiler), `DebugText`
(linker), `DebugNatives`, `DebugLines`, `DebugOverlayView`, `Injection`,
`MessageSlot`, `CodeSwap` and `RuntimeInjector` (loader).
FFX.exe addresses and names are ffx-rust's (`symbols/ffx.sym`).

## The natives

```csharp
Debug.Print($"hp {Hp,5} speed {Speed:F2}");   // to Fahrenheit's log and the screen
Debug.Break();                                // the registers to the log and the screen, then an attached .NET debugger
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
the game. Both lines also go to the screen ("On screen" below).

### On screen

The loader draws the last `Debug.*` lines top left, over the game:

```
12:04:31 w1  Larvas met: 3
12:04:31 w2  Larvas met: 4
```

the wall-clock time, `w` and the worker's index (`worker_idx`, as in the
log), then the text on one line (line breaks and tabs as spaces, cut at
160 characters). A line stays fully visible for three quarters of its
time, then fades out over the last quarter. The loader keeps the last 32
lines and shows the newest few that have not faded.

| Key | Does |
|---|---|
| F6 | shows or hides the lines (they are kept while hidden) |
| Shift+F6 | the line count: 3, 6, 10, 16, then 3 again (and shows them) |

F7 is Fahrenheit's settings window, F8 `yuta.atel.cuts` and ffx-mods'
window, F9 (with Alt or Ctrl too) FPS unlock. F10 is left alone: Windows
gives it to the window's menu bar.

The lines are drawn on ImGui's foreground draw list through Fahrenheit's
`render_imgui`, not in an ImGui window, so there is nothing to focus,
hover or click: the game keeps the keyboard, the pad and the mouse, in
battle, on the field and in menus. The keys are only looked at
(`ImGui.IsKeyPressed`). The natives add lines on the game thread; the
overlay reads them on the render thread under a lock (`DebugLines`).

The settings are JSON in the loader's Fahrenheit global state file,
`<Fahrenheit>/state/global/yuta.atel/Ffx.Atel.Loader.AtelLoaderModule`
(the loader kept nothing there before; `yuta.atel.cuts` keeps its switches
the same way):

```json
{ "debugOverlay": { "enabled": true, "lines": 6, "seconds": 8 } }
```

| Key | Default | Range |
|---|---|---|
| `enabled` | `true` | F6 sets it |
| `lines` | 6 | 1-32; Shift+F6 sets it |
| `seconds` | 8 | 1-600: how long a line stays, the fade included |

An empty or missing file is the defaults, a missing value keeps its
default, and a value out of range is clamped. A file the loader cannot
read is logged and the defaults are used. The loader writes the file
when a key changes a setting, so edit it while the game is closed.

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
  index gets the new text and keeps its flags. For event and battle
  scripts (`battle/btl/<name>/<name>.bin`, `system_01` included); a
  monster's texts are its battle's. A message without an index (from
  `Text.New`) gets the index the linker gives it after every mod's
  ([mod-package.md](mod-package.md#messages)) and is set the same way.

The loader watches its own `inject/` folder (`mods/yuta.atel/inject/`,
every `*.json` under it). At startup the packages there are linked after
every mod in the load order, so their files are served. While the game
runs, a package that appears, changes or goes away is handled on a
background thread; game memory is touched only on the game thread, before
each frame's update, when no worker runs:

| Part | When it takes effect | How |
|---|---|---|
| Debug texts | at once | added to the loader's table |
| Messages | at once, if the event or battle is running; again each time it loads | a copy of the script's table with the messages set, which the game's pointer to it then points at ("Message tables" below) |
| Code | at once where the running script can take it ("Code swaps in memory"); else the next time the game loads the script (the map entered again, the next battle) | the file is linked again (the enabled mods, then the package) and the loader's served copy in `efl/x` rewritten; a running copy of the block is swapped for the new one |

### Message tables

The game reads each table through a pointer it reads on every call, and
frees the buffer through another pointer of its own. So a table the loader
allocates and puts into the first is never freed by the game, and the
game's table stays valid for any window still showing its text. The loader
frees its own earlier copies when the game loads another table, and keeps
the latest (`MessageSlot`).

| Script | The pointer the game reads | Read by | Freed through | The loader sets it |
|---|---|---|---|---|
| The event | ctrl work 0 `+0x34` (`+0x38` too where it was the same table) | `Msg_Text` (`0x0086bf30`) and the other `Msg_` lookups, through `g_ScriptContext`; offset 0 falls back to the `.ebp`'s own table `g_EventEbpMessages` | `g_EventMessageBuffer` (`0x01327128`, `AtelEventSetUp`) | while ctrl work 0 is in use (`+0` bit 0), for the event in `g_EventName` |
| The battle | `g_BtlFileText` (`0x0112a9b8`, the second `g_BtlScriptRecords` record's `+0x14`) | natives `0x7042`, `0x7072`, `0x70b7` (text `k`), `0x70dc` (a name), `MsGetMenuChrInfo` | `g_BtlFileTextData` (`0x023112c4`) | once the battle set it up: `g_BtlStatus` (`0x0112a8e0`) not 0, 4, 5, 6 or 7; for the battle in `g_BtlFileName` (`0x023112e0`) |
| `system_01` | `g_BtlMessages` (`0x0112a998`, the first record's `+0x14`) | natives `0x7120` (`MsShowBattleMessage`), `0x711f`, `0x7121` | never (read once, by `MsBattleInit`) | outside a battle's read (status 4-7) |

The battle's table, in FFX.exe: `MsBtlReadManage` (`0x00783020`) at status 5
copies the battle's file name and starts a task (`0x00784500`) that cuts it
to the folder's name (`kami00_00`), frees the last battle's text through
`g_BtlFileTextData` (unless that was `system_01`'s) and reads the new one
with `TKMallocAndReadEventMessage`. At status 7 `MsBtlReadSetScene`
(`0x00783e20`) sets `0x0112a9b8` to 0 and then to that text. Between status
5 and 7 the pointer may name freed memory, and after a battle (status 0) it
names the last battle's text until the next read frees it, so the loader
reads it only from status 8 on and in the battle (1-3, `0x17`-`0x22`).
`MsBattleInit` reads `system_01` first and `MsBtlScriptRecordShift`
(`0x00782670`) copies its record over record 0, so `0x0112a998` is
`system_01`'s table for the whole game.

The battle ctrl works' `+0x34` is no message table: `AtelBattleSetUp`
(`0x008756d0`) sets it to 0, so `Msg_Text` gives no text in a battle
script (`g_MsgNone`). Battle scripts show text only through the natives above.

### Code swaps in memory

A running worker holds absolute pointers into its block: each level's
program counter (level `+0x18`), the work's header (`+0x00`) and block
(`+0x04`). Everything else the interpreter keeps is an offset or an index
that it looks up in the block's tables on each use: a `call`'s return
offsets (level `+0`, three of them) are code offsets, jumps go through the
running worker's label table (`Script_Jump`, `0x008726f0`), requests
through the entry table, variables through the variable table and the
storage offsets (`AtelGetRealVarAdrs`). So a block can be swapped under its
workers if each level's code, and every table entry that code names, read
the same in the new block (`CodeSwap`):

1. **Find the block.** Every work of every ctrl work in use, its block
   (`+0x04`); the works lie as `AtelGetBasicWork` (`0x0086a830`) finds
   them. A block is the script's when its header and code bytes are one
   of the blocks the loader served or swapped for the file (the data
   areas differ: the game writes them).
2. **Check.** Not safe, so left to the next load, when: the worker count,
   a worker's type or the actor count changed (the works were made at
   set-up); the code start, storage 2, 4 or 6 moved; in battle a worker's
   private data grew (storage 3 there is a slice of each work, `+0xb8`,
   sized at set-up). Then each level's program counter and return offset:
   - at the start of one of its worker's entries that the new block keeps
     there: it has run nothing of the method, so it runs the new block's
     code (a worker's patched first method starts with a `jmp` to its new
     code);
   - else inside a method (from its entry to the next): safe only when
     that method's bytes are the same in both blocks and every label,
     constant and variable its instructions name resolves the same. The
     linker lays unchanged workers out in place and leaves a patched
     method's old body where it was, so a level inside a patched method
     finishes the old body (the log says so) and runs the new one from its
     next start.
3. **Wait.** While a level is in code that changed, the loader tries again
   each frame, for up to 10 seconds; then it logs why and leaves the
   change to the next load. `Main` loops for ever, so a change inside a
   running `Main`'s loop always waits for the next load.
4. **Merge.** The new block, with every byte where the running block
   differs from the block as loaded (data the game wrote; code and tables
   are never written at run time): the running values win, also over a
   data edit of the same byte (logged). A worker's private data that the
   link moved to the block's end (events) is copied to its new place.
5. **Swap.** The merged block goes into memory of the loader's own, and
   the loader points at it: each work's header and block, each level's
   program counter in the old block (same offset), a work's trigger table
   (`+0x44`) and, for types 5 and 6, its line table (`+0x2e4`) where they
   name the old block's data (moved with a moved private area), ctrl work
   `+0x48` (the event's block), and the battle managers' block lists
   (`g_AtelManagers`, `0x01134470`, `0x54` bytes each, eight blocks at
   `+0x14`; a battle ctrl work's `+0x1f8` points at them). Any other
   word in a work that still points into the old block is logged. The old
   block is left as it is and never freed by the loader (the game frees
   its file buffer as before), so a pointer the loader does not know of
   still reads valid memory, though no longer the running values.

The served file is rewritten either way, so the next load reads the new
code. A file that was not served at startup can still be swapped in
memory; the script loads the old file again until the next start.

### Which files

Fahrenheit's file loader builds its index of replacement files once, at
startup, and opens a file by that index when the game reads it. Rewriting
a file it indexed works; a file that was not served at startup cannot be
added later. A package for such a file is reported and linked from the
next start (and swapped in memory while its script runs).

### The language

The messages are encoded with the charset of the language whose files the
game reads, `TOGetFFXLang()` (`0x006410e0`, which `TKMallocAndReadEventMessage`
uses to pick `new_<lang>pc/`): 0 Japanese, 1 English, 2 French, 3 Spanish,
4 German, 5 Italian, 9 Korean, 10 Chinese; another value uses the western
charset. Not `TkGetLang()` (`g_TkLang`), the PS2 setting, which is 0 on PC
with the English files. The loader asks once the first event or battle
runs (`TOGetFFXLang` answers 0 before its localization manager exists).

## Checked in game

See [research/loader.md](../research/loader.md#phase-10-debug-text-and-runtime-injection).
The lines on screen were checked in game on 2026-11-09
([research/loader.md](../research/loader.md#on-screen-lines-checked-in-game)).
Battle message tables and code swaps in memory are not checked in game
yet; the steps are in
[research/loader.md](../research/loader.md#battle-messages-and-code-swaps-steps-for-the-game).
