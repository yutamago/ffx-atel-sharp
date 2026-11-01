# The loader in the game (Phase 2)

What the first loader does, how it gets files to the game through
Fahrenheit, and whether files that grow need anything beyond Fahrenheit.
Checked in game on 2026-11-02 with FFX.exe (Steam HD Remaster) and
Fahrenheit 1.0.0-alpha12. Fahrenheit facts come from its alpha12 source
(`src/runtime/fileloader.cs`, `src/runtime/cd.cs`, `src/core/mods.cs`,
`src/core/paths.cs`), read for facts and API use only. FFX.exe addresses
and names are ffx-rust's (`symbols/ffx.sym`).

## Results

| Test | Script | File | Checked by |
|---|---|---|---|
| Rebuilt, unchanged | event `kami0000` (Thunder Plains, south) | 79,872 bytes, same as vanilla | developer save 51 loads the map; screenshot shows the normal scene |
| Rebuilt, unchanged | monster `m209` (Larva) | 55,280 bytes, same as vanilla | battle 34 (`kami00_00`) runs; the file in memory equals vanilla; Larva HP 1498 |
| Visible change, grown | `kami0000`: worker 0's `Main` opens message window 1 with text 73 before `WaitForever` | grew to 83,648 bytes, 5 later sections moved by `0xec0` | about a second after the map loads, the "Traveler's Save Sphere Level 1" window opens, which the vanilla map never shows. Circle closes it and the party walks on. The `.ebp` in memory equals the grown file except 23 bytes of the script's own variables, which the running scene writes |
| Changed, grown | `m209`: worker 1's init ends with `SetActorProperty(0, 1234)` | grew to 55,856 bytes, sections 1 to 6 moved by `0x240` | in battle 34 the Larva has 1234 HP instead of 1498. The file in memory equals the grown file byte for byte, including all 576 bytes past the vanilla size |

So grown event and monster scripts load whole with Fahrenheit alone. The
loader adds no size hook.

## How the loader serves files

- Fahrenheit's file loader (EFL) replaces any archive file with a loose
  file from `mods/<mod>/efl/x/<archive path>`. The archive path is the one
  `FFX_Data.vbf` uses, starting at `ffx_ps2/`:
  `efl/x/ffx_ps2/ffx/master/jppc/event/obj/ka/kami0000/kami0000.ebp`.
  Fahrenheit's doc comment gives an example with a `FFX_Data/` folder in
  front; the index key is the game's normalized path (`/FFX_PS2/...`), so
  that folder must not be there.
- EFL builds its index once, in `FhFileLoaderModule.init`, from every
  mod's `efl` folder. `FhMods` constructs every module of every mod first,
  then calls the `init`s in load order. So a module can write files into
  its own `efl/x` in its constructor and EFL serves them, even though
  Fahrenheit's runtime (`fhr`) loads before the mods. Verified: the
  Fahrenheit log lists "Mod Atel replaces file ..." for each written file.
- In the constructor, a module finds its folder from its assembly
  (`mods/<id>/<id>.dll`; Fahrenheit loads it with `LoadFromAssemblyPath`)
  and the game folder from `Environment.ProcessPath`.
- EFL opens a served file read-only with `CreateFileW` in its
  `PStreamFile` constructor hook (`0x00607bc0`), so the PS2 file layer's
  `sceOpen`/`sceFileSize` see the replacement too.

The Phase 2 loader (`src/Ffx.Atel.Loader`): every `scripts/**/*.asm` in the
mod folder, in `atel disasm` format with `; <archive path>` as the first
line, is assembled onto the vanilla file read from the player's
`data/FFX_Data.vbf`, and the whole file is written to `efl/x`. The loader
empties `efl/x` first, so removing a source stops serving it. A second
source for the same file is skipped with an error. Errors go to the log
and the game then runs the vanilla script; a failed build never stops the
game. The `Ffx.Atel.Bytecode` library runs inside the game process as is.

## File sizes

The PS2 file layer sizes a file before reading it, and the buffer and the
read use that size. A file sized from a vanilla table would be cut at its
vanilla length.

On PC, `CdSetReadFuncs_0076cff0(0)` fills the layer's function table
through `PcReadFunc_set`, which stores `CDfileSize_PC` (`0x00a428a0`; the
only pointer to it is at `0x00a42ba6`). `CDfileSize_CD` (`0x0076c320`)
goes into the table only in the CD/DVD branch (pointer at `0x0076d005`).
Direct calls to `CDfileSize_CD` (scan of every `call`/`jmp rel32` in
FFX.exe):

| Call site | Function | When |
|---|---|---|
| `0x0076baba` | `Cd_QueueRead` | DVD channel mode (`[0x02310c58]` = 1), not on PC |
| `0x0076bb0c` | `Cd_QueueRead` | PC files while `isEternalCalmScene(g_GraphicMapId)` |
| `0x0076bb2c` | `Cd_QueueRead` | HDD mode (`g_FFXHddMode`) |
| `0x0076ca7e`, `0x0076cb2f` | `CdEnvInit` | CD/DVD branch, the two size tables |
| `0x00a428d0` | `CDfileSize_PC` | HDD mode |

Every other PC read in `Cd_QueueRead` takes `CDfileSize_PC`.
Fahrenheit's `FhCdInterfaceModule` hooks `CDfileSize_PC`: it sets the CD
state's module (`+0x24`) to 0 around the call, so the function always
falls through to `sceOpen` and `sceFileSize`, which read the EFL file's
real size (rounded up to 16). It also makes `check_ex_file_size` return
0. That is why both grown files loaded whole.

What is left:

- **Eternal Calm maps** (the 7 maps from `g_EternalCalmFirstMap`,
  `0x00c33468` = 393, so maps 393-399). While one is the
  graphic map, every `Cd_QueueRead` sizes through `CDfileSize_CD`, which
  Fahrenheit does not hook. A grown file read there would be cut. Not
  tested in game. A hook like ffx-rust's (`crates/ffx-hook/src/mods.rs`:
  the replacement's size, rounded up to 16, for a file number whose
  `CDfileName_PC` path has a replacement) would close it.
- **HDD mode** uses `CDfileSize_CD` too. Nothing on PC was seen setting
  `g_FFXHddMode`.
- `CDfileSize_CD_EX` (`0x0076c410`) is called only from `CDfileSize_PC`
  (`0x00a4294a`), on the path Fahrenheit's module override skips.

## How it was run

FFX.exe started through `fhstage0.exe` from a copy of the game folder,
with only `yuta.atel` in `mods/loadorder` (the install's load order was
backed up and restored afterwards). To keep the window in the background
and drive the game, the copy also held ffx-rust's `ffx-hook` as
`DINPUT8.dll` with its harness on (`FFX_HARNESS`,
`FFX_HARNESS_BACKGROUND=1`, a sandboxed Documents folder). Its own file
and size hooks were turned off (`FFX_HOOK_DISABLE` with
`Phyre_PSerialization_PStreamFileWin32_openFile`, `CDfileSize_CD`,
`CDfileSize_PC` and the replacements, `FFX_HOOK_ONLY=none`), so only
Fahrenheit served and sized files. The harness commands used: `devsave
51`, `press`, `shot`, `read`/`readabs`, and the battle request ffx-rust's
`screens::start_battle` makes (field 34 at `0x0112c256`, group and
formation at `0x0112c258`, `call 0x780ff0 1`, `0x0112a8e2` = 1). Monster
HP is at `*0x011334d4 + n * 0xf90 + 0x5d0` (actor 20 is the first
monster). A monster's file in memory starts `0x30` before its script
block, found through battle ctrl work 2's block table (`+0x1f8`, Debug
Revival's `docs/research/script.md`).

## Notes

- `ScriptBlock.Rebuild` puts a worker whose code grew at the end of the
  block and leaves its old code in place. One instruction more in
  `kami0000`'s worker 0 grew the block by the worker's whole 3.7 KB of
  code. The game runs it, but the compiler will want a tighter layout.
- With text 73 in window 1 nothing else changed, so `MsgSetPos`,
  `MsgSetStyle`, `MsgSetText`, `MsgSetFlags(1, 0)`, `MsgOpen` and
  `MsgWaitEx_End(1, 1)` are enough for a message the player closes.
- `SetActorProperty` (`0x70ab`) property 0 is the actor's HP: set in a
  monster's init entry, it replaces the HP from the monster's data.
