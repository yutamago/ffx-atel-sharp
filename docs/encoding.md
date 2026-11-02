<!-- SPDX-License-Identifier: MIT -->

# Encoding rules

How each Atel construct compiles to ATEL bytecode, and which choices of the original compiler the
compiler reproduces so that every shipped script comes back byte for byte. Condensed from
[corpus.md](corpus.md) (the measurements), [formats/script-block.md](formats/script-block.md) (the
block layout) and the Phase 2-4 sections of [plan.md](plan.md); the language itself is in
[language.md](language.md).

The code is `Ffx.Atel.Bytecode.Code` (`Statements.cs`, `Lowering.cs`: one encoding per statement
and expression; `Flow.cs`: structured statements and label numbers; `ConstantPools.cs`) and
`Ffx.Atel.Compiler` (`StatementReader`: C# to those records). The decompiler lifts bytecode into
the same records and keeps a statement only if lowering it gives back its bytes, so every form
below has exactly one encoding.

Assembly is in the syntax of `atel disasm`. `push k` stands for the constant rule below.

## Principles

| Rule | Holds for | Source |
|---|---|---|
| The stack is empty at every label, jump and end | all 1,429,747 labels, 2,111,981 jumps, 119,227 ends | so a statement is the code from an empty stack to the next |
| Operands are pushed left to right; the operator follows | all | |
| No code for conversions | all | ATEL values are untyped 32-bit words |
| Conditions are evaluated whole; no branch inside an expression | all | no `?:`, no short-circuit `&&`/`\|\|` |

## Constants

| C# | Bytecode | Rule |
|---|---|---|
| `5`, `-5`, `0x7fff` | `pushimm 5` | an integer that fits `i16` (-32768 … 32767) |
| `40000`, `0x10000`, `-2147483584` | `pushc cN` | an integer outside `i16`: from the integer pool |
| `1.5f`, `-28.2f`, `0f` | `pushfc fN` | every float literal, from the float pool |
| `0` passed to a `float` parameter | `pushimm 0` | an integer literal stays an integer push (C# converts implicitly); write `0f` for `pushfc` |
| `true`, `false` inside a condition | `pushimm 1`, `pushimm 0` | |
| `Story.S01340_Kami0400`, `Save.StoryProgress`, `Target.Own` | as the number | a constant of the reference assembly or an enum member |
| `Text[12]` | as `12` | |
| `Text.New("Hi")` | `pushimm` of the index the linker gives (a `newMessage` relocation) | mods only |
| `Pooled(46, -44.018925f)` | `pushfc f46` | a fixed pool index |

The `pushimm`/`pushc` rule holds for all 7,661,225 integer pushes of the corpus. Float parameters
get an integer push 157,210 times, which is why the decompiler writes `Foo(0)` there and `Foo(0f)`
only where the original had `pushfc`.

### Constant pools

One integer and one float pool per script (`ConstantPools`):

| Pool | Order | Holds in |
|---|---|---|
| integers | first use, in code order (workers in order, each worker's code in layout order) | 908 of 1,184 blocks |
| floats | per statement: non-negative values where they are pushed; negative values at the end of the statement, each also numbering its absolute value where it is pushed if that value is in the pool | 1,113 of 1,575 blocks |

The float rule reads as a parser that numbers the literal `28.2` and later folds `-28.2` into a new
constant at the end of the statement. Where a rule fails, the decompiler pins the whole pool with
`[Ints(…)]` or `[Floats(…)]` (`[FloatBits(…)]` for a value no literal gives exactly; none in the
corpus); the compiler then looks every constant up in the pin. One pool (`dome00_04`) holds a value
twice; code that uses the later copy is `Pooled(index, value)`.

## Variables and registers

| C# | Bytecode |
|---|---|
| `V3` (read) | `pushv v3` |
| `V3 = e;` | `e; popv v3` |
| `A[i]` | `i; pushva vA` |
| `A[i] = e;` | `i; e; popva vA` |
| `G[r, c]` (row length `n`) | the flat index `c + r * n`: `c; r; push n; mul; add`; with a constant row `c; push r*n; add` (`c` alone when `r*n` is 0); both constant: one `push` |
| `At(G, i)` | `i; pushva vG` |
| `Addr(ref A[i])` | `i; pushvref vA` |
| `StoreClamped(ref V3, e)` | `e; popv_1 v3` (`popva_1` for an element) |
| `I0`, `I0 = e;` | `pushi0`, `e; popi0` (`pushi0`…`pushi3`) |
| `F2`, `F2 = e;` | `pushf2`, `e; popf2` (`pushf0`…`pushf9`) |
| `RegA`, `RegB` | `pusha`/`popa`, `pushb`/`popb` |
| `LastResult` | `pushfacc` (read only) |

`vN` is the variable's index in the script's variable table, which is the order of the `[Var]`
fields in the script class.

### Assignments

| C# | Bytecode |
|---|---|
| `x op= e;` on a variable or register | the same as `x = x op e;`: `pushv x; e; op; popv x` |
| `A[i] op= e;` on an element | `i; op_2b; pushva vA; e; op; popva vA` (`op_2b` keeps a copy of the index) |

`op` is one of `+ - * / % & | ^ << >>`. There is no increment opcode: `x++;` as a statement is
`x += 1;` (see "New code" for `++` as a value).

## Operators

| C# | Opcode |
|---|---|
| `+ - * / %` | `add sub mul div mod` |
| `<< >>` | `shl sar` |
| `== != < > <= >=` | `eq ne lt gt le ge` |
| `a & b`, `a \| b` on conditions | `land`, `lor` (both sides evaluated) |
| `a & b`, `a \| b` on integers | `and`, `or` |
| `^` | `xor` |
| `!c` | `not` |
| `~x` | `bnot` |
| `-5` (a literal) | the negative constant |
| `-x` (new code) | `x; neg` for a float, `push 0; x; sub` for an integer (see "New code") |
| `(T)e`, `+e`, `unchecked(e)`, `AsBool(e)`, `AsInt(e)`, `(e)` | nothing |

An operand is a condition when its syntax is a comparison, `!`, `true`/`false`, `AsBool(…)`, a
native returning `bool`, or `&`, `|`, `^` of conditions. The opcodes do not depend on operand types:
the same `add` for `int` and `float`, and the signed compares always (the unsigned compares, `btst`,
`btstn` and `neg` occur nowhere in the corpus).

## Calls and requests

| C# | Bytecode | Rule |
|---|---|---|
| `Common.WaitFrames(30);` | `push 30; func_facc 0x0000` | a call statement: the result goes to the float accumulator |
| `V3 = Common.Random16();` | `func 0x…; popv v3` | a call whose result is used |
| `Foo(); RegB = LastResult;` | `func_facc …; pushfacc; popb` | the result of a call made as a statement, read afterwards |
| `W48.Run();`, `Call<W48>();` | `call w48` | a function worker |
| `_ = W.E5Async();` | `push 1; push w; push 5; req0; popfacc` | level, worker, entry, request, drop the result |
| `await W.E5Async();`, `.Finished` | `…; reqe0; popfacc` | |
| `await W.E5Async().Started;` | `…; reqw0; popfacc` | |
| `W.E5Async(Priority.P3)` | `push 3` as the level | level 1 without the argument |
| `Request(l, w, e, variant: 2)` | `…; req2; popfacc` | the fallback form; `RequestWaitStart`, `RequestWaitEnd` likewise |
| `RequestParty(l, slot, e)` | `l; slot; e; op_39; popfacc` | |

`func_facc` for a statement and `func` for a used result hold for all 3,146,543 calls. A native
that waits for an answer is read back with `pushfacc` 3,151 times and as `func` 408 times: the
source says which (`x = Foo();` or `Foo(); x = LastResult;`). Every request is a statement
followed by `popfacc` (106,583). Scripts use only `req0`, `reqw0`, `reqe0` and `req2`.

## Ends and single instructions

| C# | Bytecode |
|---|---|
| `return;` | `end`; `ret` in a function worker |
| `End(v);` | `v; endp` |
| `End(v, reset: true);` | `v; endp_1` |
| `Halt();` | `op_54` |
| `Breakpoint();` | `op_40` |
| `Nop(n);` | `nop_76 n` |
| `Op77(a, b);`, `Op79(a, b, c);` | operands, then `op_77`, `op_79` |
| `Entry(n);` | nothing: entry `n`'s offset is here |

The compiler adds no `end` at the end of a method: code that does not end in a jump or `end` runs
on into the next method, as the original's did.

## Control flow

Branch polarity: `popa_jz` (jump if zero) for every forward conditional jump, `popa_jnz` for every
backward one; `jz`, `jnz` and `popa_jmp` never occur (all 1,125,834 conditional jumps).

| C# | Bytecode | Labels, in number order |
|---|---|---|
| `if (c) { T }` | `c; popa_jz L0; T; L0:` | L0 (end) |
| `if (c) { T } else { E }` | `c; popa_jz L0; T; jmp L1; L0: E; L1:` | L0 (else) at the condition, L1 (end) when the else starts |
| `if (false) { T }` | `jmp L0; T; L0:` | as `if`; the dead code is kept (2.48 million instructions in the corpus) |
| `if (c) goto lN;` | `c; popa_jnz lN` | |
| `if (!c) goto lN;` | `c; popa_jz lN` | |
| `goto lN;` | `jmp lN` | |
| `while (c) { B }` | `L1: c; popa_jz L0; B; jmp L1; L0:` | L0 (exit), L1 (head) |
| `while (true) { B }` | `L1: B; jmp L1; L0:` | as `while`; the exit only if a `break` uses it |
| `do { B } while (c);` | `L1: B; c; popa_jnz L1; L0:` | L0 (exit, if used), L1 (head) |
| `for (i; c; s) { B }` | `i; L2: c; popa_jz L0; jmp L1; L3: s; jmp L2; L1: B; jmp L3; L0:` | L0 (exit), L1 (body), L2 (head), L3 (step) |
| `break;` | `jmp` to the innermost loop's exit or switch's break label | |
| `continue;` | `jmp` to the head (`while`), the step (`for`) | |

The original compiler put a `for` loop's step before its body; `continue` in a `for` jumps to the
step.

### Switch

```
    selector
    popb                    ; the selector to register B
    jmp Lchain
Lcase1:                     ; the sections in source order
    …
    jmp Lbreak              ; break;
Ldefault:
    …
Lchain:                     ; one compare per case, in source order
    push 1
    pushb
    eq
    popa_jnz Lcase1
    …
    jmp Ldefault            ; only if there is a default
Lbreak:
```

| Case label | Compare |
|---|---|
| `case k:` | `push k; pushb; eq; popa_jnz case` |
| `case < k:` | `push k; pushb; gt; popa_jnz case` |
| `case <= k:` | `push k; pushb; ge; popa_jnz case` |
| `case > k:` | `push k; pushb; lt; popa_jnz case` |
| `case >= k:` | `push k; pushb; le; popa_jnz case` |
| `case >= lo and <= hi:` | `pushb; push lo; ge; popa_jz skip; pushb; push hi; le; popa_jnz case; skip:` |
| `default:` | `jmp default` at the end of the chain |
| `goto case k;` as a section's last statement, to the next section | nothing (fall-through) |
| any other `goto case k;`, `goto default;` | `jmp` to that section |

Labels: chain, break, then each section's label as its section starts (after the labels of the
sections before it), the range skips with the chain. 71,181 of 82,809 switches in the corpus have no
default and fall through to the break label.

### Label numbers

Each worker has one label counter. Each construct draws its labels from it **as it starts**, in the
orders of the tables above, across all the worker's methods in layout order. Only labels a jump goes
to get a number (every label in the corpus has a jump to it). Source labels `lN` keep their number
and the counter skips it. Label numbers therefore follow neither code order nor first jump; this
rule matches every method in the corpus but 26, which the decompiler pins with `[Labels(…)]`: the
method's labels get the listed numbers in allocation order, and the counter goes on after the
highest.

### What the decompiler checks

It lowers each worker with the same emitter (`FlowEmitter`) and compares with the original,
labels by position. A construct whose lowering differs, whose label a remaining `goto` needs, or
whose block a `goto` from outside jumps into, is dropped and the method structured again without
it. Then label numbers are compared and pinned, and the result checked byte for byte. 3,199
constructs are dropped this way (8,448 `goto`s remain), none because its lowering differed.

## Workers and entries

| Source | Script block |
|---|---|
| `[Workers(typeof(A), typeof(B), …)]` | worker order: A is worker 0 |
| base class `Worker0` … `Worker6` | worker header `+0x00` (type) |
| `[Worker(Word0C, PrivateLength)]` | header `+0x0c`, `+0x10` |
| `[PrivateData(length, hex)]` | the worker's private block (`+0x2c`) |
| entry methods in layout order | the worker's code; an entry's offset is where its method (or `Entry(n);`) starts |
| `[Entry(n)]`, `Init`/`Main`, `Run` | the entry table, by number |
| used labels | the label table, by number |

A worker's code range runs from its first entry to the next worker's. Entry offsets ascend with
the entry number in every shipped worker.

## Block layout

The block's regions in order ([formats/script-block.md](formats/script-block.md#order)): header,
worker headers, map entrances, author, name, variable table, integer pool, float pool, code (the
workers back to back), shared data (aligned to 32), "other" (`0x40` bytes), event data, area, then
per worker its entry table, label table and private data, the end aligned to 16.

| Choice | Rule | Pin where it fails |
|---|---|---|
| shared data offset | aligned to 32 after the code | `[Pin("shared", 0x…)]`: 934 blocks start 32 bytes later (a code reserve) |
| event data offset | after "other" | `[Pin("event", …)]`, 5 battle scripts |
| block end | aligned to 16 | `[Pin("end", …)]`, 2 developer maps |
| variable table | the declaration order of the `[Var]` fields | none needed: the source is in table order |
| header words not understood | | `[Header(Word14 = …, …)]` |
| bytes no rule produces | | `[Patch(offset, hex)]`, none in the corpus |
| data areas | | `[SharedData]`, `[EventData]`, `[OtherData]`, `[AreaData]`, `[MapData]` |

The data areas are kept as hex because they hold more than variables' initial values (the shared
area holds a timestamp and offsets into the block). Event and monster files hold the script as
section 0; the compiler takes the file's other sections from the game's file.

## New code

What the original compiler never emitted, so the round trip does not constrain it: the forms below
compile only from new code (a mod, or a hand edit), and the decompiler never writes them. The stack
stays empty at every jump and label; where an expression needs a jump, it is computed by statements
before the statement that holds it, into a temporary.

### Operators

| C# | Bytecode |
|---|---|
| `x++;`, `++x;`, `x--;` as a statement | as `x += 1;`, `x -= 1;` (on an element the `op_2b` form) |
| `++x` as a value | `x = x + 1;` first, then `x` is read |
| `x++` as a value | `t = x; x = t + 1;` first, then `t` is read |
| `-x` | `x; neg` when `x` is a float, else `push 0; x; sub` |
| `c ? a : b` | `if (c) t = a; else t = b;` first, then `t`; assigned to a variable, register or local directly: `if (c) x = a; else x = b;` |

`neg` pops its operand as a float (the interpreter's `0x1a`, x87 `* -1.0`), so it is only for floats;
an integer `0 - x` stays an integer. Whether a value is a float follows the stack's types: a float
literal, register, variable or local, a native that returns `float`, and `+ - * /` with a float
operand.

### Evaluation order

C# evaluates operands left to right. When a later operand needs statements (`?:`, `x++` as a value,
`&&` with statements on its right, a helper call), every earlier operand that is not a constant is
evaluated into a temporary first: `Out = Hp++ * 10 + ++Hp;` is `t0 = Hp; Hp = t0 + 1; t1 = t0 * 10;
Hp = Hp + 1; Out = t1 + Hp;`. An element's index is evaluated once (`Table[i++] += 1` reads and writes
the element at the old `i`), and a compound assignment reads its target before the value's statements.

Named arguments to natives bind by the parameter's name; when they are out of the parameters' order,
all arguments are evaluated into temporaries in the order written, then pushed in the parameters'
order.

### Short-circuit conditions

`&&` and `||` are jumps, one operand tested at a time; `!` swaps the jump. `L` is a label of the
construct's own, numbered as the construct's labels are, when it is needed:

| Condition | Jump when false (`if`, `while`, `for`) | Jump when true (`do … while`, `if (…) goto`) |
|---|---|---|
| `a && b` | `a; popa_jz F; b; popa_jz F` | `a; popa_jz L; b; popa_jnz T; L:` |
| `a \|\| b` | `a; popa_jnz L; b; popa_jz F; L:` | `a; popa_jnz T; b; popa_jnz T` |
| `!(…)` | as jump when true | as jump when false |

A condition without `&&`/`||` lowers as before, so vanilla code is unchanged. As a value, a
short-circuit condition is `t = 0; if (c) t = 1;`. When `b` needs statements of its own (`a && x++ >
0`), they must run only when `a` does not decide: `t = 0; if (a) { …; if (b) t = 1; }` (for `||`:
`t = 0; if (a) t = 1; else { …; if (b) t = 1; }`), and the condition is `t`. A loop condition that
needs statements runs them on every test: `while (true) { …; if (!c) break; body }`, likewise for
`for`; for `do … while` they go at the end of the body.

### Locals

Each local, parameter, result and temporary is a slot. After a worker's or fragment's code is
lowered and its labels numbered, the compiler computes which slots are live at each instruction (over
the code's jumps) and places each:

| Slot | Home |
|---|---|
| live across a wait (a native that polls, `req*`, `op_39`, `call` of a function worker or of a helper that may wait, `op_54`, `op_77`-`op_79`) | a hidden variable |
| otherwise | an integer register (`i3` down to `i0`) or float register (`f9` down to `f0`) that the worker's code does not name (its `[Register]` fields, any register opcode in the code, the `[Register]` fields of every function worker it calls, all registers in a function worker), and that no slot live at the same time has |
| otherwise | a hidden variable |

A placed slot is `pushi3`/`popi3` (`pushf9`/`popf9`, …) or `pushv`/`popv` of its variable. A hidden
variable is an `i32` (`f32` for a float) in storage 4, one per slot: in a mod a new variable of the
package (`<local>name#n`), which the linker allocates in its new data area; in a script compiled
whole a variable at the end of the variable table, at the end of the shared data (which grows by 4
bytes each). Registers are per worker and only change while the worker runs, so a value that crosses
no wait cannot be changed by other code; a hidden variable belongs to one slot of one method.

### Helpers

A helper method is a function worker of its own, numbered after the script's workers (in a mod, a
new worker of the package after the mod's other new workers; the linker puts function workers last).

| C# | Bytecode |
|---|---|
| `H(a, b);` | `a` and `b` evaluated (into temporaries if a later one needs statements), then `p0 = a; p1 = b; call wH` |
| `x = H(a);` | `p0 = a; call wH; t = result; x = t` |
| the helper's code | entry 0; `return v;` is `result = v; ret`; `ret` at the end |

Parameters and the result are slots of the helper, placed before its callers' slots: a helper's
slots avoid the registers named by every worker that calls it (directly or through other helpers) and
those of its callees; its registers then count as changed by each `call` of it, so a caller's slot
live across that call takes another register (or hidden storage if the helper may wait). A caller's
slot live where it writes a parameter avoids that parameter's register. Calls nest at most 3 deep
from an entry; recursion is an error.

### Entry parameters and results

An entry another worker requests (`async Task<int> FooAsync(int x)`) gets a hidden variable per
parameter (besides a `Priority` one, the level) and for its result: slots whose home is fixed when the
compilation starts, so every requester can write them.

| C# | Bytecode |
|---|---|
| `_ = W.FooAsync(a);` | `x = a;` then the request (`push level; push w; push e; req0; popfacc`) |
| `await W.FooAsync(a);`, `….Finished` | `x = a;` then `reqe0` |
| `await W.FooAsync(a).Started;` | `x = a;` then `reqw0` |
| `n = await W.FooAsync(a);` | `x = a; …; reqe0; popfacc; t = result; n = t` |
| in the entry: `x`, `return v;` | `pushv x`; `result = v; end` |

Arguments are evaluated in the order written (temporaries as for natives), all before the first field
is written.

## Mods

A mod's code is compiled with the same rules into fragments whose operands the linker fills in
(labels, pool constants, variables, worker and entry numbers; see
[formats/mod-package.md](formats/mod-package.md)). The linker keeps every vanilla byte of the block
in place and puts what grew after the block's end, including the whole code region when any code
changed. A prefix or postfix has no `end` of its own (it runs on into the chain); a new entry and a
replacement end in `end` (`ret` in a function worker).
