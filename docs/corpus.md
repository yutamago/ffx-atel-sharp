# The corpus, measured (Phase 0)

What the decompiler (Phases 3-4) and the compiler have to reproduce byte for
byte, measured over all 1,621 shipped scripts (361 monster, 397 event, 863
battle; 15,010 workers, 21.3 million instructions) on 2026-11-02.

The numbers come from `atel survey` (`src/Ffx.Atel.Bytecode/Analysis`). Its
full output is [`survey.md`](survey.md); regenerate both generated files with

```
atel survey -o docs/survey.md --story data/story-progress.json
```

This page says what the numbers mean. Assembly snippets show the depth of
the stack before each instruction in brackets.

## What the compiler can derive, and what must be pinned

| Choice | Rule | Holds for | Otherwise |
|---|---|---|---|
| `pushimm` or `pushc` | `pushc` exactly when the value does not fit `i16` | all 7,661,225 integer pushes | |
| `func` or `func_facc` | `func_facc` for a call statement, `func` when the result is used | all 3,146,543 calls | a waiting native's result is read as `func_facc; pushfacc` 3,151 times, as `func` 408 times: pin per call or find the rule |
| conditional jumps | `popa_jz` forward, `popa_jnz` backward; `jz`, `jnz`, `popa_jmp` never occur | all 1,125,834 | |
| store mode | `popv`, `popva`; `popv_1` 8 times (all on `u16`), `popva_1` never | all but 8 stores | pin the 8 |
| ends | `end` per entry; `ret` ends every function worker; `op_54` 7,774 times, `endp`/`endp_1` 32 | | the source says which (`Halt()`, `return 1;`) |
| stack across jumps | empty at every label, jump and end | all | |
| label numbers | per construct, at the construct's start (below) | 59,185 of 64,105 switches without nested labels; 17,219 of 17,252 `while` loops | a structurer that follows the rule; pin the rest |
| dead code | `if (0) { … }` keeps its code, jumped over by a `jmp` | 2.48 million instructions | the decompiler writes `if (false)` blocks |
| integer pool order | first use in code order | 908 of 1,184 blocks | pin the order |
| float pool order | first use, a negated literal also numbering its absolute value, the folded value numbered at the end of the statement | 1,113 of 1,575 blocks | pin the order |
| variable table order | grouped by storage, by offset inside a storage | 1,003 of 1,343 blocks (1,321 grouped) | pin; 6,381 entries repeat another's location, 356 are unused |
| worker order | events: types in runs (`1 4 3 2 5 0` or a subset), function workers last | 350 of 397 event blocks grouped, all with function workers last | declared order wins (`[Workers(...)]`) |

## Stack

With the argument counts of `data/natives.json` the stack simulation is
clean: no underflow, no merge of two depths, no call statement leaving a
value, in every worker. The stack is empty at all 1,429,747 labelled
instructions, across all 2,111,981 jumps and at all 119,227 ends.

So no expression spans a branch. The original compiler had no `?:` and no
short-circuit `&&`/`||` inside an expression; `land` and `lor` (69,485 and
3,222) evaluate both sides, as the plan says. A decompiler can turn every
statement into an expression tree in one linear pass between labels
(`WorkerFlow.Operands`).

It also validates the native table. All 1,059 natives the scripts call, at
3,146,543 sites, fit their argument count; the 452 never called stay
unchecked. Nothing in `tools/gen-natives.cs` needed changing, but one
opcode was wrong in `StackEffects`: `op_2b` duplicates the top of the stack
(`AtelFetchStackInteger`, `0x0086a000`, reads it without popping and the
handler pushes it again), so it is (pop 1, push 2), not (1, 1). Compound
array stores use it for the index:

```
    [0]  pushimm 36
    [1]  op_2b
    [2]  pushva v0
    [2]  pushimm -65
    [3]  and
    [2]  popva v0          ; v0[36] &= -65
```

With the old effect 12,627 `popva` underflowed. Rerunning
`tools/gen-natives.cs` with the fix changed only vote counts in
`data/natives.json`, no signature.

## Control flow

### If and else

457,770 forward `popa_jz`. 124,169 of them land right after a forward `jmp`,
an `if` with an `else`. The condition before the jump ends in a comparison
1,041,068 times, `land`/`lor` 61,824, `not` 6,573, and a bare value 16,000
or so (`func` 12,467, `and` 2,039, `or` 1,134, `pushv` 558). A bare value
means the source wrote `if (Foo())`, not `if (Foo() != 0)`, which would
add an `ne`. Phase 3 needs a C# spelling for each: natives typed `bool`
would do for `func`.

### Switch

82,809 switches, every `popb` but 51. The shape:

```
    pushv v2
    popb                    ; selector in B
    jmp l1                  ; chain label
l3:                         ; case bodies in chain order, each ending in jmp break
    …
    jmp l2
l9:                         ; default body last, here empty
    jmp l2
l1:
    pushimm 1               ; one compare per case
    pushb
    eq
    popa_jnz l3
    …
    jmp l9                  ; with a default; without one the chain falls through
l2:                         ; break label
```

- The chain ends in `jmp default` in 11,628 switches; in the other 71,181 it
  falls through to the break label. 1,919 defaults are only `jmp break`
  (`default: break;`), so the source wrote those out.
- Cases compare `push k; pushb; eq` 635,963 times (`pushc` for values
  outside `i16`). Other compares exist: `le`, `gt`, `lt`, `ge` 2,729 times,
  and ranges 27,865 times
  (`pushb; push lo; ge; popa_jz next; pushb; push hi; le; popa_jnz case; next:`).
  The original language had `case lo..hi` and relational cases; Atel needs
  C# patterns for them (`case >= 380 and <= 401:`, `case > 5:`).
- No two cases share a body (no `case 1: case 2:` written as one label).
  6,052 of 638,148 bodies do not end in `jmp`, so fall-through exists.
- Case bodies are in chain order with the default last in 76,168 switches.

### Loops

Backward jumps: 666,557 are a switch's case jumps and 11,628 its default
jump (the bodies come before the chain). The rest are loops:

- 17,252 `while`/`for`: head label, the condition, `popa_jz` to just after
  the backward `jmp`. The exit label is the head label minus one in 17,219.
- 1,507 `do … while` (`popa_jnz` back).
- 40,607 backward `jmp` without a leading exit: 25,052 contain a jump out
  (`while (1)` with `break`), 197 an end, about 15,000 neither (entries
  that loop for ever). 1,003 go to a label that shares its instruction with
  another, which looks like a source label and `goto`.
- Inside loops: 32,272 `break`, 6,429 `continue` to the head, 6,401 jumps
  forward to a label that runs straight into the backward `jmp` (`continue`
  in a `for`, to its step).

### Labels

Every one of the 1,550,256 labels has a jump to it, so the compiler made
labels only for jumps. Label numbers follow neither code order (23 % of
workers) nor first appearance (30 %) nor first jump (52 %). They follow the
constructs: each construct numbers its labels when it starts.

| Construct | Labels, in number order |
|---|---|
| `while` | exit, head |
| `if` | else (or end) at the condition; the end of an `if`/`else` when the `else` starts |
| `switch` | chain, break, one per case in chain order, default; range skip labels after those |

The `if` row comes from reading examples; the others are measured where nothing nests inside (59,185 of the 64,105
switches whose bodies place no other labels follow it); Phase 4's structurer
has to emit labels in this order. Example of the `while` rule, from `m101`:

```
l1:                         ; head
    pushimm -13
    func 0x704a
    popa_jz l0              ; exit, numbered first
```

### Code no entry reaches

2,506,757 instructions (12 %), nearly all of it code jumped over:

| Shape | Runs | Instructions |
|---|---|---|
| `jmp L; …; L:` | 14,030 | 2,241,620 |
| the same, ending in a forward `jmp` (`if (0) … else`) | 15,408 | 236,820 |
| after `jmp`, `end` or `op_54`, up to a label, an entry or the worker's end | 18,153 | 28,317 |

```
l1:
    jmp l2
    func_facc 0x005e        ; Common.ControlDisable
    pushimm 1
    pushimm 39
    pushimm 8
    reqe0
    popfacc
l2:
    end
```

The original compiler folded a constant false condition to a plain `jmp`
and kept the code. The dead code simulates cleanly too, and the decompiler
must write it (`if (false) { … }`, C# warns CS0162, which the Atel SDK
should silence).

### Entries

1,504 jumps go from one entry's code into another's, and 1,102,265
instructions are reached from more than one entry. Entries are code
offsets into shared code, not functions; the decompiler needs `goto` into
another entry's region, or a shared helper, in those workers. Entry
offsets ascend with the entry number in every worker.

## Encoding

- Floats: `pushfc` 1,622,362, 706,995 of them integral. Float parameters
  get an integer push 157,210 times (`Foo(0)` where the source wrote an
  integer literal, C#'s implicit conversion does the same); integer
  parameters get `pushfc` 216 times, which C# can't write without a cast.
- `func_facc` is the call statement: its result goes to the float
  accumulator and is dropped. `pushfacc` (3,348) reads it back, after a
  `func_facc` 3,154 times, 3,151 of them for natives that wait (a choice
  box's result, `switch (MsgShowWait_Result(…))`).
- `popfacc` (106,583) drops a request's result: every `req*` is a
  statement.
- No `nop` anywhere, 38 of the other no-ops, 20 breakpoints (`op_40`).
- Never used: the unsigned compares, `btst`/`btstn`, `neg`, `pusha`,
  `jz`/`jnz`, `popa_jmp`, `end_1`, `popva_1`, `pushg`, `op_3a`/`op_3b`,
  `op_78`, `op_7a`, float registers 4-9, and every request form but
  `req0`, `reqw0`, `reqe0`, `req2`. The compiler need not emit them.

## Constant and variable tables

The tables are one set per block (docs/formats/script-block.md). No pool
has an unused entry; the integer pools have no duplicate value, the float
pools one. The order:

| Model | Integers | Floats |
|---|---|---|
| first use in code order | 908 / 1,184 | 133 / 1,575 |
| per statement, non-negative values first, then negative ones | 908 | 992 |
| the same, and a negative literal also numbers its absolute value where it is pushed | 908 | 1,113 |

The float rule reads as a parser that numbers the literal `28.2` and later
folds `-28.2` into a new constant at the end of the statement:

```
    pushimm 0
    pushfc f3        ; -28.22593
    pushfc f2        ; 15.044581
    pushfc f4        ; -37.573696
    func_facc 0x7048
```

What is left interleaves across workers at a finer grain than workers or
entries (in `azit0000` the constant `0x2620000` has index 8, but its only
use is in worker 14 after the constants of workers 1, 2, 15 and 16).
Named constants declared early in the source would do that. Until a rule
covers it, the pool order is a pin.

The variable table is in first-use order in only 128 blocks. It groups the
storages (1,321 blocks), most often `1 3` (795, monsters and battles) and
`0 6 3`, `0 6 4 3` (events), and sorts by offset inside a storage in 1,003.
That points at declaration order. 6,381 entries repeat another entry's
location and length (the same variable declared twice, probably in two
files), and 356 are never used. Pin both.

## Workers

| Type | Workers | Entries (median) | What the typical natives say |
|---|---|---|---|
| 0 | 441 events | 1 | function worker: every `call` (5,560) targets one; ends in `ret`; 392 are the same 39-instruction function |
| 1 | 6,707 events | 9 | actors and the director: models, motion, movement |
| 2 | 648 events, 898 monsters, 2,388 battle | 8 / 5 / 3 | events: map triggers and lines, map changes (`SetTriggerMode`, `TriggerSetLine`, `MapChange`); every monster and battle worker |
| 3 | 486 events | 10 | point triggers (`TriggerSetPoint`, `SetLegendAbility`) |
| 4 | 1,944 events | 6 | screen and map parts: pad, sprites, map marker, guide map (`PadGetAnalog`, `SpriteSet13`, `MapPartSetAlpha`) |
| 5 | 1,382 events | 3 | line triggers with a position (`LineGetCrossSide`, `TriggerSetLine`) |
| 6 | 116 events | 4 | point triggers with map sprites |

Entries that only `end`: 60 % of type 2 event workers' entries, 49 % of
type 3's, 32 % of type 1's. Entry 0 only ends in 60 % of type 4 workers
and 58 % of monster workers. Triggers (types 2, 3) have a fixed entry set
(8-12 and 8-17) the engine calls; that is where the base classes' default
entries pay off.

### Requests

106,583 requests, operands pushed level, worker, entry. Scripts push
levels 1 to 7 only, never 0 or 8:

| | 1 | 2 | 3 | 4 | 5 | 6 | 7 |
|---|---|---|---|---|---|---|---|
| `req0` | 59,873 | 5,305 | 11,925 | 845 | 688 | 223 | 212 |
| `reqw0` | 2,605 | 220 | 54 | 4 | 5 | 0 | 2 |
| `reqe0` | 20,672 | 1,132 | 2,498 | 95 | 117 | 26 | 46 |

`req0`, `reqw0` and `reqe0` register "without the same level" (ffx-rust `signal.rs`, `register`); `req2` ("without the same tag") occurs 32 times, all
at level 1; the party request `op_39` 3 times. 743 requests go to the
worker's own entries, 464 of them from entry 1: `req0` at level 1 431
times, `reqe0` at level 1 77 times. Whether a `reqe0` at the caller's own
level waits for ever is for the in-game check (the plan's question); level
1 from entry 1 is the case to try.

The worker and the entry operand are constants in 98.6 % of requests each.

### Duplicates

Exact copies (same type, entries, code, constants by value, variables by
location): 3,525 workers beyond the first of their group, 23 % of all, in
991 groups; 976 groups span scripts. Ignoring operands other than labels
and native IDs, 7,237 workers (48 %) copy another's shape. That is the case
for library base classes; the largest group is the function worker above.

## Story progress

The story progress word is `g_SaveData + 0xbec` (ffx-rust `docs/formats/save.md`,
Fahrenheit's `story_progress`). Scripts reach it as storage 0 offset
`0xa00`, because storage 0 starts at `g_SaveData + 0x1ec`:
`AtelSetUpCtrlWork` (`0x00871190`) sets the ctrl work's `+0x2c` to
`MsGetSaveEventAddress() + 0x1ec` when it is unset. A `u16`, used in 337
scripts.

1,541 of its 1,547 stores write a constant, all in events. Without the
developer maps (`test*`, `sample*`) there are 449 values, 432 of them set
by exactly one script, so `Story.S0005_Znkd1400` style names work for
nearly all; 12 values come from two scripts, 5 from more. The full table is
[`data/story-progress.json`](../data/story-progress.json). Reads (5,262)
mostly feed a `switch` (`popb` 2,133) or a `<`/`>=` compare.

## Entry numbers as data

1,503 requests compute their entry: an `add` (975, a base entry plus an
index) or an element of a storage 6 array (518, event data). None reads a
storage 0 (save) variable, so no save holds an entry number and entries can
be renumbered as long as the event data tables move with them. Natives
taking entry numbers as arguments were not checked.

## `CDfileSize_CD_EX` on PC

`CDfileSize_CD_EX` (`0x0076c410`) returns `g_Cd_ExSizes[file + g_CdLib_FileBase]`
when the index is below `g_Cd_ExSizeCount`, else `-1`. The table is
`cdidx/jp/sizetbl.vita.bin` (in the VBF), loaded by `pc_sizetbl_set_Ex`
(`0x00a43100`) from `CdEnvInit`. Its only caller is `CDfileSize_PC`
(`0x00a428a0`): after `check_ex_file_size` (the HDD install table,
`0x0076d660`) and outside HDD mode, it asks `CDfileSize_CD_EX` only while
the current module (`0x02310c64`, set by `CdSetModule_0076c510`) is `0xe`,
`0x10`, `0x1a`, `0x1c`-`0x22`, `0x28` or `0x29`. For other modules, or when
it returns `-1`, the size comes from the file itself (`sceOpen`,
`sceFileSize`, rounded up to 16). So a grown script reports its real size
on PC unless its module is in that list and the vita table has an entry for
it; which modules hold the event, monster and battle files is the open
part (static, from `cdrom.fid` and the module table; not checked here).

## Open

- The integer and float pool order where the models fail, and the variable
  table's declaration order.
- Label numbering inside nested constructs; checking it needs the Phase 4
  structurer.
- `func` against `func_facc; pushfacc` for the 408 + 3,151 waiting calls: the
  source says which (`x = Foo();` or `Foo(); x = LastResult;`, plan.md
  "Phase 3: statements"); a rule would let Phase 5 merge the second form.
- Bare-value conditions in C#: settled in Phase 3 as `AsBool(x)`, no code.
- The modules of the script files (`CDfileSize_CD_EX` above).
