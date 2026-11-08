<!-- SPDX-License-Identifier: MIT -->
# Saved data for mods

The plan's open item: a mod that keeps a counter or a flag across saves
needs somewhere to keep it. This page maps the save, what is free in it,
what Fahrenheit offers, and recommends a design. Research of 2026-11-08.
Implemented the same day as recommended (D), with the user's answers to
the open questions (plan.md, "Decisions (user)"): [language.md](../language.md#saved-fields),
[formats/mod-package.md](../formats/mod-package.md#saved-fields).

FFX.exe addresses and names are ffx-rust's (`symbols/ffx.sym`,
`docs/formats/save.md`, `docs/formats/script.md`); the code was read in the
Ghidra dump. Fahrenheit facts come from its source (`src/core/state.cs`,
`src/core/module.cs`, `src/core/save_mgr.cs`, `src/runtime/save_impl.cs`,
`src/core/events/game_loop/`), read for facts and API only.

## Summary

- The save file is `0x6900` bytes. The game copies `0x68c0` of it to and
  from `g_SaveData` (`0x0112ca90`), but the CRC and every known field end at
  `+0x64b8`. The last **`0x408` (1,032) bytes, `g_SaveData + 0x64b8` ..
  `+0x68c0`** (`0x01132f48`..`0x01133350`), are saved and loaded with the rest,
  outside the CRC, and nothing in FFX.exe reads or writes them. They are
  zero in every save checked. Scripts reach them as storage 0 offsets
  **`0x62cc`..`0x66d3`**, which no vanilla script declares.
- Fahrenheit already gives every module a **side file per save slot**
  ("local state": `save_local_state` / `load_local_state`), and its own rule
  is that mods must not keep their data in free space of the save.
- Storage 3 through the ctrl work's `+0x54` callback is a poor fit: storage 3
  is the workers' private data (385,131 vanilla variables, and Atel#'s hidden
  locals), battle already uses the callback, and the callback would run on
  every private-variable access.
- **Recommended:** a `[Saved]` field compiles to a plain storage 0 variable
  in that tail, at an offset the loader assigns. While the game runs, the
  tail is the variables' home, so the vanilla engine reads and writes them
  with no hook. Across saves the loader keeps them **by name** in its
  Fahrenheit local state. The tail is zeroed in the file the game writes, so
  save files stay vanilla.

## The save

From ffx-rust `docs/formats/save.md`, with what this research added:

| File offset | Block offset | Size | What |
|---|---|---|---|
| `0x0000` | | `0x40` | header (`MenuSave_PackSave` `0x008b3e60` fills it: counter, slot, party, language, gil, …; `+0x1a` the CRC) |
| `0x0040` | `0x0000` | `0x64b4` | the game state; the last field is `character_names` (`g_SaveChrNames` `0x01132ddc`, 18 × 20, ends at block `+0x64b4`) |
| `0x64f4` | `0x64b4` | 4 | the CRC again |
| `0x64f8` | `0x64b8` | **`0x408`** | **the tail: copied, never read** |
| `0x6900` | | | end of file (`SaveData_Init` `0x00647fb0`: manager buffer size `0x6900`) |

The PS2 file is `0x64f8` bytes; the PC file is the PS2 file plus the tail.

### How the tail gets saved and loaded

- **Save.** `MenuSave_PackSave` (`0x008b3e60`, from `TkMenuCtrlSaveLoad` and
  `TkMenuPreSave`) clears the header and copies `memcpy(out + 0x40,
  g_SaveData, 0x68c0)`, tail included. It computes a CRC over all `0x68c0`
  bytes, but every writer then calls `SaveDataWriteCrc` (`0x00648f20`), which
  recomputes it over `0x64b8` bytes and stores it at `+0x1a` and `+0x64f4`:
  `SaveDataMachine_StartSave_006f07a0` (the menu save) and
  `SaveDataManager_debugSave_Internal_6f0650` (`0x006f0510`, the autosave)
  in the game, and Fahrenheit's own save and autosave (below).
- **Load.** `SaveDataCheckCrc` (`0x00647d70`) checks `TkCRC(0x64b8, buffer +
  0x40)`, so the tail can hold anything and the save still loads.
  `TkMenuCopyLoadedSave` (`0x008b54a0`) then copies `0x68c0` bytes into
  `g_SaveData`, tail included.
- **New game.** `MsSetSaveStartGame` (`0x00786b00`) clears `0x68c0` bytes
  of `g_SaveData` ("SAVE RAM CLEAR"), tail included.
- **Tutorials.** `TkMenuCtrlTuto` (`0x008db4e0`) keeps `0x68c0` bytes of
  `g_SaveData` at `0x0186ac60`, loads the tutorial's save over it, and
  copies the kept bytes back at the end. The tail is restored like the rest.
- **Nothing else.** The dump's data references to `0x01132f48`..`0x0113334f`
  are one loop end (`resetDefaultPlayerNameStatus` `0x007873a0`, one past
  the 18 names' `+0x12` byte). ffx-rust has no label in that range. The
  next label, `0x01133350`, is the rename flag after the block.
- **Real saves.** Five PC saves on the development machine (the autosave
  and four manual saves) have all `0x408` tail bytes zero.

### Storage 0 and what scripts use

Storage 0 is `g_SaveData + 0x1ec` for every ctrl work (`AtelSetUpCtrlWork`
`0x00871190` sets ctrl `+0x2c` to `MsGetSaveEventAddress() + 0x1ec` when it is
0), in events, battle and the menu. The offset field of a variable is 24
bits, so storage 0 reaches the tail as offsets `0x62cc`..`0x66d3`.

A scan of every variable table in the corpus (1,621 scripts; the scan is
throwaway code, not committed) gave:

- No storage 0 variable reaches past offset `0x1a00` (the last one is
  `BlitzballPlayerUncoveredTechsPage2`, `0x1910`, 60 × `u32`, in the
  blitzball scripts). So nothing in the corpus declares the tail.
- Storage 0 offsets no script declares, below the CRC word: 21,291 bytes,
  most of them `0x1a00`..`0x62c7` (save `+0x1bec`..`+0x64b3`). That is not
  free space: the engine owns most of it through pointers (`g_SaveAbilityMap`
  at save `+0x21ec`, the sphere grid; `g_SaveWalkMap` at `+0x350c`, the
  minimap; config, gil, inventory, characters from `+0x3d0c`), and the
  smaller holes between script fields (the largest `0x2b9`..`0x53f`, 647
  bytes, and `0xb7a`..`0xfff`, 1,158 bytes) may be fields the scripts read
  through arrays or the engine through structures nobody has named yet.
  Only the tail is free beyond doubt.

### Storages the scripts never use

`AtelGetRealVarAdrs` (`0x0086c2e0`) is the only function in the dump that
turns a variable's location word into an address: the opcodes, the
natives that take a variable and the debugger's watch all call it (the
only other decoder of the storage bits, `AtelDebug_PrintVarLocation_0087e880`,
prints the debugger's name for it). By storage (bits 25-27), with
the variable counts of [formats/script-block.md](../formats/script-block.md#variables):

| Storage | Debugger | Address | Vanilla variables |
|---|---|---|---|
| 0 | `[GBL]` | ctrl `+0x2c` + offset (`g_SaveData + 0x1ec`) | 123,214 |
| 1 | `[LCL]` | ctrl `+0x30` + offset | 2,072 |
| 2 | `[FIX]` | block + header `+0x28` + offset (`+0x28` is 0 in every shipped worker) | none |
| 3 | `[MY]` | block + header `+0x2c` + offset, or ctrl `+0x54(work, offset)` when set | 385,131 |
| 4 | `[DAT]` | block + header `+0x30` + offset | 35,443 |
| 5 | `[ITR]` | work + `0x48` + offset | none |
| 6 | `[LC2]` | block + block `+0x20` + offset | 233,334 |
| 7 | none | 0 (jump table `0x0086c3c8`, default case) | none |

Storage 7 would need a patch: the engine gives address 0, so a write
through it crashes. Storage 2 points into the block, which the game
reloads with the script. Storage 5 is inside the work.

## Storage 3 and the `+0x54` callback

[formats/script-block.md](../formats/script-block.md#private-data-at-run-time)
has the details. Storage 3 is the private data of the worker that runs the
code. In events and the menu nothing sets ctrl `+0x54`, so storage 3 is the
block's own private area per worker. In battle `AtelBattleSetUp`
(`0x008756d0`) sets `+0x54` to `0x008756b0` (`work +0xb8 + offset`), a
cleared buffer per work. The linker already puts mods' hidden locals and
private variables in storage 3, past the vanilla private data.

Redirecting storage 3 to a save area would mean a callback that tells saved
offsets from private ones (by range), installed on every ctrl work, chained
in front of battle's own. Every private access in the game (385,131
variables) would go through it, as a native-to-managed call. A saved
variable there would also share its numbering with each worker's private
data, which differs per worker, so one saved variable used from two
workers would need one offset that is past every worker's private data. A
plain storage 0 variable has none of these problems.

## What Fahrenheit offers

`FhSaveExtensionModule` replaces the game's save UI and hooks
`SaveDataToSave`, `SaveDataToLoad`, `TkRefreshHdd` and
`SaveDataManager_debugSave_Internal_6f0650` (the autosave). Saves live in
Fahrenheit's own folder, `saves/<save set hash>/<set>/<subfolder>/<name>`,
not in Documents. The save set hash covers the mods whose manifest has
`SEPARATE_SAVES`; without that flag a mod shares the default set.

Its **local state** (`FhState`, "Mods must be able to persist some
information between saves or game sessions. They must not store such
information in empty space of the save data …"):

- `FhModule.save_local_state(FileStream)` runs after every save and
  autosave (`state_save_slot`), for every module of every mod, with the
  file `state/<save set hash>/<set>/<subfolder>/<save name>/<mod id>/<module
  type>/state`, plus `state.meta.json` holding the mod's version.
- `FhModule.load_local_state(FileStream, FhLocalStateInfo)` runs on load
  after the save is in `g_SaveData` (the `TkRefreshHdd` hook, inside
  `TkMenuJumpToLoadedScene`, before the title script's map jump), and **only
  when that module has a state file for the slot**. `PostLoadGame` fires
  after it in every case.
- Things to handle: the state file is opened `OpenOrCreate` without
  truncating, so a writer must set its length; `PostNewGame` and
  `PreSaveGame` are declared but not raised yet (TODO in Fahrenheit);
  `PostReturnToTitle` is raised in FFX.
- Each module also gets a global state file in `init`.

So an Atel mod, a file-only Fahrenheit mod with no module of its own, can
only use local state through the loader's module (`yuta.atel`).

## Options

| | Bytes live in | Linked as | Survives without the loader | Two mods | Save file |
|---|---|---|---|---|---|
| A. Tail only | save tail | storage 0, offset by load order | yes (the game saves it) | offsets move when the mod set changes: data lands in the wrong variable | carries mod data |
| B. Side file only, storage 7 | a buffer of the loader's | storage 7 (patched jump table) | no: storage 7 is address 0, a write crashes | by name | vanilla |
| C. Side file, storage 3 | private area redirected | storage 3 above a threshold | no | by name | vanilla |
| **D. Tail at run time, side file by name** | tail while running, side file across saves | storage 0, offset per run | no Atel mod links without it; a script compiled whole can still pin a variable there | by name | vanilla (tail zeroed on write) |

D keeps what is good in A (the engine reads the variables with no hook, and
the decompiler shows plain save variables) and fixes A's problem: the
offset of a saved variable depends on which mods are enabled and in what
order, so only names can be stored safely. Fahrenheit's local state is the
place for names.

## Recommended design (D)

### The C# surface

A saved field is a static field with `[Saved]`, in any class of the mod: a
mod-wide class for state several scripts share, or a script's partial
class. It may have an initializer. Types are the ATEL variable types and
fixed-size arrays of them.

```csharp
// Mod-wide: shared by every script of the mod that uses it.
namespace LarvaCount;

public static class Progress {
    [Saved] public static ushort LarvasMet;
    [Saved] public static byte   Tier = 1;           // initial value on a new game or a save without it
    [Saved] public static byte[] Flags = new byte[8];
    [Saved(Key = "Met")] public static ushort Seen;  // a renamed field keeps its old key
}

// In a script (the Larva, as in examples/LarvaHp): reads and writes them as any field.
namespace Monsters.M209;

partial class W01 {
    [Postfix(nameof(Init))]
    void CountLarva() {
        LarvaCount.Progress.LarvasMet += 1;
        if (LarvaCount.Progress.LarvasMet >= 10) LarvaCount.Progress.Flags[0] |= 1;
    }
}
```

- The **key** is `<mod id>/<declaring type>.<field>` (`yuta.larvacount/
  LarvaCount.Progress.LarvasMet`), or the field's `Key`. The key, the type and
  the length are what is saved; the offset never is.
- Analyzers: a `[Saved]` field that is not static, has an unsupported type,
  or is a `[Register]` or `[Var]`; two fields with one key; a key that
  changed type or length since the last release (needs the mod's previous
  package, so later).

### The package and the linker

- `atel compile-mod` writes each `[Saved]` field once, into a mod-wide
  `atel/saved.json`: `{ "key": "…", "type": 2, "length": 1, "rowLength": 0,
  "initial": "base64" }`.
- A script package that uses one lists it in `variables` as storage 0 with
  `"saved": "<key>"` and no `offset`. Code refers to it through `newVar` as
  now.
- The loader, before linking any file, reads every enabled mod's
  `saved.json` and assigns offsets in the tail (from `0x62cc`, aligned to the
  element size), in load order. One table for the whole run, so a key has
  the same offset in every script. The linker fills the variable's `offset`
  from that table: a fixed-location storage 0 variable, which the package
  format already supports.
- A mod whose saved fields do not fit the 1,032 bytes is left out with an
  error in the report (every package of that mod, since its scripts share
  the fields).
- The linker never touches vanilla storage 0 variables. The `Save` class
  names the area (`Save.ModArea = 0x62cc`, `Save.ModAreaLength = 0x408`) so
  the decompiler shows a linked script's saved variables as `ModArea`
  offsets.

### The loader at run time

| When | Hook | Does |
|---|---|---|
| New game | `MsSetSaveStartGame` (`0x00786b00`), after it (Fahrenheit's `PostNewGame` is not raised yet) | the tail is zero; write every key's initial value |
| Load | `load_local_state`, then `PostLoadGame` | clear the tail; for every key of the current mods found in the state file with the same type, write its bytes at the key's offset; every other key gets its initial value. Keys of mods not enabled now are kept in memory |
| Save, autosave | `save_local_state` | write the state file (truncated first): every current key's bytes from the tail, plus the kept keys of absent mods, plus the save's fingerprint |
| Writing the file | before `SaveDataWriteCrc` (`0x00648f20`; the game's and Fahrenheit's save paths all call it with the file buffer) | zero file bytes `0x64f8`..`0x6900` in the buffer, so the file holds no mod data; `g_SaveData` keeps it |

The state file, JSON, in `yuta.atel`'s local state:

```json
{
  "format": 1,
  "save": { "crc": 25058, "playTime": 1234567 },
  "vars": {
    "yuta.larvacount": {
      "LarvaCount.Progress.LarvasMet": { "type": 2, "length": 1, "bytes": "CgA=" },
      "LarvaCount.Progress.Flags":     { "type": 0, "length": 8, "bytes": "AQAAAAAAAAA=" }
    }
  }
}
```

The fingerprint is the save's CRC (`+0x1a`) and play time (block `+0xbc`),
read from the save buffer on load. A state file whose fingerprint does not
match the save it is loaded with was left behind by a save made without
the loader, so it is ignored, with a warning.

### Cases

- **A vanilla save** (no state file; for example copied in from Documents):
  `load_local_state` does not run, and `PostLoadGame` writes the initial
  values. The tail of a vanilla save is zero anyway.
- **A save made without the mod** (state file without its keys): its fields
  get their initial values; the other mods' fields load as saved.
- **A save made with a mod that is now disabled**: its keys stay in the
  state file and come back when it is enabled again. Its offsets go to
  whichever mods are enabled now; nothing reads stale bytes, because the
  tail is rebuilt from names on every load.
- **Two mods**: separate keys, separate offsets; load order only decides the
  offsets, which are never saved. Over 1,032 bytes in total, the later mods
  in load order are left out.
- **A mod update** that changes a field: same type and length loads as is;
  a longer array keeps its first elements and the rest get the initial
  values; another type gets the initial value, with a warning. A rename
  keeps its data with `Key`.
- **Without the loader**: no Atel mod is linked, so nothing reads the area.
  Under Fahrenheit, the state files of `yuta.atel` stay in its folder,
  unused. A save made then has a zero tail (the game copies the zero bytes
  in `g_SaveData`), and the next load with the loader finds a fingerprint
  that does not match, so it starts the fields from their initial values.
- **A whole script compiled with `atel compile`**, served without mods: it
  can pin a variable in the area (`[Var(Storage.Save, Save.ModArea + 4)]`)
  and the plain game saves and loads it in the tail, with no Fahrenheit
  module. That is for development; with the loader running, the tail is
  zeroed on write, so such a script and the loader do not mix.
- **PS2 conversion**: the PS2 file has no tail, so mod data is lost there,
  as are the side files.

### What the loader needs from Fahrenheit, and what it does not

Nothing new. It uses its module's local state, `PostLoadGame`, and its own
hooks on `MsSetSaveStartGame` and `SaveDataWriteCrc`. If Fahrenheit later
raises `PostNewGame`, that hook can go.

### If 1,032 bytes is not enough

Storage 7 is free and resolves to address 0. Pointing entry 7 of
`AtelGetRealVarAdrs`'s jump table (`0x0086c3c8`) at a few bytes of native
code that return `area + offset` would give an area of any size, with the
same side file and no managed call per access. That is a patch of FFX.exe
code, and without the loader a storage 7 write crashes, so it should wait
until a mod needs more.

## Checks before implementing

In game (for the parent; the harness of
[loader.md](loader.md#how-it-was-run)):

1. **The tail round-trips.** Load developer save 51. Write `41 54 45 4c` at
   `g_SaveData + 0x64b8` (`0x01132f48`, rebased) and `5a` at
   `g_SaveData + 0x68bf`. Save into a free slot through the menu. Expected:
   the file has those bytes at `0x64f8` and `0x68ff`, and loads with no
   "corrupt" message. Load it again: memory has the bytes back.
2. **Under Fahrenheit's save UI.** The same with Fahrenheit's save module on
   (the normal setup): the file is the one under Fahrenheit's `saves/`
   folder.
3. **Storage 0 at `0x62cc` in a script.** A test mod with a postfix on
   `kami0000` worker 0's `Init` that declares
   `[Var(Storage.Save, 0x62cc)] static int Probe;` and does
   `Probe += 1; Debug.Print($"probe {Probe}");`. Load developer save 51:
   the log says `probe 1` and `0x01132f48` (rebased) reads 1. Save into a
   free slot: the file has `01 00 00 00` at `0x64f8`. Load that slot: the
   log says `probe 2`. So a storage 0 variable in the tail runs and is
   saved by the game with no hook.
4. **Local state.** With a module that writes a state file on save, check
   that a manual save and the autosave both create
   `state/…/<save name>/yuta.atel/<module>/state`, and that loading the
   slot calls `load_local_state` before the map's scripts start.

## Open questions

Answered by the user on 2026-11-08 (plan.md, "Decisions (user)"): 1. zero
the tail; 2. ignore a state of another save, with a warning; 3. no
`SEPARATE_SAVES`; 4. yes, by key, with the other mod in `requires`
(`[Saved(Mod = …, Key = …)]`). The questions as they were:

1. Zero the tail in the written file (recommended: save files stay vanilla,
   as Fahrenheit asks), or also keep the bytes there as a copy, so a save
   moved without the Fahrenheit folder keeps the mod data when the mod set
   is unchanged?
2. A state file whose fingerprint does not match the save: ignore it (as
   above) or load it anyway with a warning?
3. Should `yuta.atel` set `SEPARATE_SAVES`? Then the saves of a game with
   Atel mods are a set of their own. The design works either way.
4. Should one mod be able to read another mod's saved fields (by key, with
   `requires`)? Left out above.
