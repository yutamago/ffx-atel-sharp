# The script block, byte for byte

What a script block holds besides its code, and in what order, so the
compiler can build one from declarations alone. Measured over all 1,621
shipped scripts (361 monster, 397 event, 863 battle) on 2026-11-01; the opcode-level
format is ffx-rust's `docs/formats/script.md`, the header names follow
Fahrenheit's `AtelScriptChunk` and `AtelScriptHeader`.

`Ffx.Atel.Bytecode.Script.ScriptImage` is this layout as code: a block is
taken apart into its parts (`FromBlock`) and laid out again by the rules
below (`Write`). Where a block deviates from a rule, the region's offset is
pinned; bytes the rules don't produce would become patches. Over the
corpus (`CorpusTests.EveryBlockRebuildsFromItsImage`):

| | Blocks |
|---|---|
| rebuild byte for byte | 1,621 / 1,621 |
| with patch bytes | 0 |
| pin: shared data 32 bytes later than the rule (code reserve) | 934 (352 of the 758 event and monster scripts) |
| pin: event data elsewhere (battle scripts) | 5 |
| pin: block end (developer maps `test28`, `test34`) | 2 |

The tables below, except where they say otherwise, count the 758 event
and monster scripts.

## Files

Event files start with `EV01` and a table of section offsets up to
`0xffffffff`. Monster and battle files start with a section count and as
many offsets: 8 for every monster; 8, 7 or 5 in `battle/btl` (817 have 8,
37 have 7, 9 have 5). Some battle files have section offsets past the end
of the file (`kino03_00`: `0xb00` in a file of `0xaf8` bytes). The script
is section 0 in all of them.

## Block header

| Offset | Type | Meaning | Written from |
|---|---|---|---|
| `0x00` | `u32` | code length | sum of the workers' code |
| `0x04` | `u32` | map entrances (`0x20` each, up to the author); 0 in 353 blocks | layout |
| `0x08` | `u32` | author string (`"keisuke"`, …) | layout |
| `0x0c` | `u32` | name string (`"001"`, …) | layout |
| `0x10` | `u32` | block length | layout |
| `0x14`–`0x1f` | | Fahrenheit: `0x18` main script, `0x1e` zone bytes; the rest unknown | kept |
| `0x20` | `u32` | event data (storage 6) | layout |
| `0x24` | `u32` | unknown | kept |
| `0x28` | `u32` | area (Fahrenheit's `offset_area`) | layout |
| `0x2c` | `u32` | "other" (Fahrenheit's `offset_other`), always `0x40` bytes | layout |
| `0x30` | `u32` | code start | layout |
| `0x34` | `u16` | worker count | |
| `0x36` | `u16` | workers that are not function workers (type ≠ 0): true in all 743 blocks with workers | derived |
| `0x38` | `u32` × n | worker header offsets | layout |

## Worker header (`0x34` bytes)

| Offset | Meaning | |
|---|---|---|
| `0x00` | type | |
| `0x02`–`0x06` | variable, int and float counts | the same in every worker of a block |
| `0x08`, `0x0a` | entry and label counts | |
| `0x0c` | unknown | kept |
| `0x10` | private data length (Fahrenheit's `priv_data_len`) | kept |
| `0x14`–`0x1c` | variable, int and float tables | **one set per block**, shared by all workers |
| `0x20`, `0x24` | entry and label tables | per worker |
| `0x28` | storage 2 data | 0 in every shipped script |
| `0x2c` | private data (storage 3) | 0 or the block after the worker's tables |
| `0x30` | shared data (storage 4) | the same in every worker |

## Order

Regions follow each other in this order; each starts where the previous
ends unless the rule says otherwise.

1. Header, `0x38 + 4n` bytes.
2. Worker headers, back to back.
3. Map entrances, if any.
4. Author, NUL-terminated.
5. Name, NUL-terminated, aligned to 4.
6. Variable table (8 bytes each), aligned to 4; then int constants; then
   float constants.
7. Code: the workers' code back to back, in worker order. Each worker's
   range runs from its first entry to the next worker's.
8. Shared data (storage 4), aligned to 32 after the code. In 352 blocks it
   starts 32 bytes later; nothing in the block says why, so it is a pin (the
   original compiler may have reserved code space it didn't use).
9. "Other", `0x40` bytes.
10. Event data (storage 6).
11. Area: 0 or 32 bytes.
12. Per worker, in worker order: its entry table, its label table and, if
    `+0x2c` is set, its private data: `+0x10` rounded up to 16 bytes, at
    least 16.
13. End, aligned to 16: the block length at `+0x10`. The section may run on
    to the next `0x40` boundary of the file; those bytes belong to the file.

A region whose pointer is 0 is absent: "other", event data and area in the
developer maps `sample01`, `sample02`, `test28`, `test33` and `test34`. The
15 empty monster slots (`m332` and `m347`–`m360`) have no workers, so nothing points
at their shared data, but it is there, where rule 8 puts it.

## The data areas

| Area | Bytes | Under a declared variable | Non-zero, under none |
|---|---|---|---|
| shared (storage 4) | 4,332,084 | 951,342 | 1,436,696 |
| other | 52,201 | 0 | 12,352 |
| event (storage 6) | 846,864 | 609,586 | 662 |
| area | 33,730 | 0 | 21,686 |

Variables give their initial values in these areas. The shared area holds
more than variables: in `m001` it starts with a timestamp (`01/28
13:20:44`), a run of `0xff` and words that point back into the block
(`0x4dc`, `0x4e0`), structures the original compiler laid out. Until they
are understood, the compiler carries what no variable covers as raw bytes.

## Variables

779,194 variables. By storage: 0 (save data) 123,214; 1 (common) 2,072;
3 (private) 385,131; 4 (shared) 35,443; 6 (event) 233,334; never 2, 5 or
7. By type: `u8` 274,907, `i8` 14,198, `u16` 39,693, `i16` 85,688, `i32`
181,250, `f32` 183,458; never `u32` (type 4). No variable has length 0;
638,711 have length 1.

## Private data at run time

Where storage 3 lives while the game runs (FFX.exe, ffx-rust's names; checked
2026-11-02). `AtelGetRealVarAdrs` (`0x0086c2e0`) resolves a variable through
the work it is given, the worker that runs the code: storage 3 is the block
plus the work's header `+0x2c`, unless the current ctrl work's `+0x54`
callback is set, which then gives the address.

| Where | Who sets it | Storage 3 is |
|---|---|---|
| events, the menu | nothing sets `+0x54` | the block's own private data of that worker (header `+0x2c`); one copy per worker, since an event runs each worker once |
| battle (`AtelBattleSetUp`, `0x008756d0`) | `+0x54` = `0x008756b0` (`work +0xb8 + offset`) | a buffer of the work's own |

`MsAtelSetUp` (`0x00798390`) sizes the battle buffer as the sum, over the
registered blocks, of each actor worker's `+0x10` rounded up to 16
(`AtelGetMyBufferSize`, `0x0086c0c0`; the first `+0x36` workers, those that
are not function workers) and allocates it (`user_malloc`; the camera
scripts' manager 2 uses a fixed 0x800-byte area for this and the actor
buffers together and stops with `MsBattleError` past it).
`AtelBattleSetUp` then gives each actor work its slice
(`AtelSetMyWorkAdrs`, `0x00870870`: `+0xb8` = the slice, `+0x10` bytes
cleared) and advances by `AtelGetMyWorkSize` (`+0x10` rounded up to 16).
So in battle `+0x2c` is not read, only `+0x10`, and the private data starts
at zero whatever the block holds. Function workers get no slice: storage 3
in their code is their caller's.

`call` (opcode `0x33`, in `Script_Run`) keeps the work and its header: it
saves the return offset and the callee's number in the level, and only the
program counter (and the label table, `Script_Jump`) is the callee's. A
function worker's variables therefore resolve through the calling worker:
its storage 3 is the caller's private data, at the same offsets.

Several monsters of one kind: `MsBtlReadMonster` (`0x00783680`) loads a
monster file once per battle; a later slot with the same monster takes the
earlier slot's buffer (`mon +0x48`), so both slots' `+0xf78`
(`Mon_AttachData`, `0x00783b00`) point at **the same script block**.
`Mon_LoadAll` (`0x00784070`) registers each slot's block with `MsAtelRegist`
(`0x00797a40`), the shared one twice, and `AtelBattleSetUp` makes a set of
works for each registration. Two Larvas therefore share the block's shared
data (storage 4) and event data (storage 6), while each Larva's workers have
their own private data (storage 3). A worker runs one level at a time; the
level running is work `+0x32`, which native `0x00eb`
(`GetFieldU832_00856980`) returns.
