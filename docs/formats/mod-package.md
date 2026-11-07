# Mod packages

What a compiled Atel mod holds and how the linker puts it onto the
player's scripts (plan: "Mods", Phase 9). Code: `src/Ffx.Atel.Linker`
(`ScriptPackage`, `ScriptLinker`, `BlockWriter`, `ModSet`).

## A mod on disk

An Atel mod is a Fahrenheit mod folder, `mods/<id>/`, listed in
Fahrenheit's `mods/loadorder`:

```
mods/<id>/
  <id>.manifest.json        Fahrenheit's manifest (a file-only mod needs no DLL);
                            depends on yuta.atel
  mod.toml                  source_language, after, requires, incompatible
  atel/<archive path>.json  one package per script the mod touches
```

`atel/ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin.json` is the
package for `m209`. The file name is only for people; the package names
its file inside (`file`).

`mod.toml` (all keys optional):

| Key | Meaning |
|---|---|
| `source_language` | the language of the mod's new messages (`Text.New`), an ISO code (`"en"`, the default; `ja`, `fr`, `es`, `de`, `it`, `ko`, `zh`) |
| `requires = ["id", …]` | mods that must be enabled, else this one is not linked |
| `incompatible = ["id", …]` | mods this one is not linked with (the later of the two is left out) |
| `after = ["id", …]` | mods it must load after; a wrong load order is reported, not changed |

## A package

JSON (`ScriptPackage`), one per script. It holds only the mod's own code
and data: vanilla code and data are referred to by number, with a hash of
each vanilla part, so a package never contains game code.

```json
{
  "format": 1,
  "file": "ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin",
  "script": "m209",
  "vanilla": {
    "workers":   [ { "index": 1, "name": "Ai", "hash": "…" } ],
    "entries":   [ { "worker": 1, "name": "Init", "entries": [0], "start": 0, "end": 147, "hash": "…" } ],
    "variables": [ { "index": 2, "name": "V2", "hash": "…" } ]
  },
  "variables": [ { "name": "Turns", "type": 5, "storage": 4, "length": 1, "rowLength": 0, "array": false } ],
  "workers":   [ { "name": "Helper", "type": 2, "word0C": 0, "code": { … }, "entries": [0, 7] } ],
  "patches":   [ { "kind": "postfix", "worker": 1, "name": "MoreHp", "target": "Init", "code": { … } } ]
}
```

- **`vanilla`**: every vanilla part the package attaches to or uses. A
  worker by number, an entry method by worker and name (the entries it
  starts and its code range in the worker), a variable by table index.
  Hashes are SHA-256 over a fixed encoding, the first 8 bytes in hex
  (`VanillaHash`): a worker's type, code, entry and label offsets; an
  entry method's code bytes and the labels inside it; a variable's table
  entry. The linker checks every hash against the player's file before it
  applies anything of the package.
- **`variables`**: new variables, by type (0 `u8` … 6 `f32`), storage,
  length and row length. Without `offset` the linker allocates them:
  storage 3 (private), 4 (shared, the default) or 6 (event). With `offset`
  they are a fixed location (a save or common variable the mod names).
- **`workers`**: new workers with their own entry numbers (entry 0 `Init`,
  1 `Main`, …); the worker's number is given when linking. The mod's helper
  methods are new function workers (type 0, entry 0, named
  `<helper>Class.Method`), after its other new workers.
- New variables named `<local>…` are the hidden storage of the mod's
  locals (encoding.md, "Locals"): private arrays of nine `i32`/`f32`, one
  element per level, zero at start; and of its entries' parameters and
  results, one `i32`/`f32` each, private when only the entry's own worker
  requests it, else shared.
- **`patches`**, on vanilla workers:

  | `kind` | Source | Does |
  |---|---|---|
  | `entry` | an `async` method without an attribute | a new entry; it gets the next free number |
  | `replace` | `[Replace(nameof(E3))]` | replaces the method's code |
  | `prefix` | `[Prefix(nameof(Init))]` | runs before it; `return;` ends the entry, so the method does not run (as Harmony's `return false`) |
  | `postfix` | `[Postfix(nameof(Init))]` | runs after it, wherever it ends the entry |

  `target` is the vanilla method's name (from the decompiled source); the
  patch applies to every entry that method starts.

- **Code** (`Fragment`): `bytes` (base64) are the instructions, `labels`
  the fragment's jump labels as offsets into them, and `relocs` the
  operands the linker fills in (the `u16` after the opcode at `at`):

  | `kind` | `value` | Becomes |
  |---|---|---|
  | `label` | fragment label | the worker's label number |
  | `int`, `float` | the integer, the float's bits | its index in the pool (added when missing) |
  | `var` | vanilla variable index | the same (checked against `vanilla.variables`) |
  | `newVar` | index in `variables` | its table index |
  | `worker` | vanilla worker number | its number after linking (`call`, or `pushimm` in a request) |
  | `newWorker` | index in `workers` | its number |
  | `newEntry` | index in `patches` (an `entry`) | its entry number (`pushimm` in a request) |
  | `newMessage` | index in `messages` (one without an index) | the message's index in the script's tables (`pushimm`) |

  Entries and replacements end in `end` (`ret` in a function worker);
  prefixes and postfixes run off their end into what follows.

- **`cuts`**: named dead blocks the mod switches on
  (`[assembly: EnableCut(nameof(Cut.…))]`;
  [research/dead-code.md](../research/dead-code.md#switching-a-block-on)):
  `{ "name": "ZanarkandCrowdFacingReaction", "signatures": [ "377cdffd6bbcf32a", … ], "blocks": 29 }`,
  the signatures of the blocks' code (`data/cut-names.toml`) and how many
  the script has. The linker finds them by signature, checks the count and
  that each starts with `jmp L` (`b0 lo hi`), and writes `nop_76 L` (`f6 lo
  hi`) there, before any code is laid out; a package with only cuts leaves
  the rest of the file as it is. A package with `cuts` has `"format": 2`;
  others stay 1. Left out when there are none.
- **`debugStrings`**: the texts of the package's `Debug.Print` calls; the
  code pushes each one's id ([debug.md](debug.md)). Left out when there are
  none.
- **`messages`**: texts for the script's message tables, written as `atel
  text export` writes them (control codes in braces).
  `{ "text": "Nice throw!" }`, without an index, is a new message from
  `Text.New`: the linker numbers it (see "Messages" below).
  `{ "index": 73, "text": "…" }` sets message 73, for runtime injection: the
  loader sets it in the running event's table
  ([debug.md](debug.md#runtime-injection)).

A package is also a **runtime injection package**: put into the loader's
`inject/` folder, it is linked after every mod, and while the game runs a
changed one is linked again and served for the script's next load
([debug.md](debug.md#runtime-injection)).

Later, in the same shape: `data` (per-field edits of data areas and
kernel tables), and `inject` patches.

## Linking

`ScriptLinker.Link(vanilla file, packages in load order)` per script; the
loader runs it for every file any enabled mod has a package for.

1. **Check.** A package whose hashes do not match the player's file, whose
   `cuts` are not all there, or
   that refers to anything it does not declare, is left out for this script
   (an error in the report); the other mods' packages still apply.
2. **Number.** In load order: new variables get the next table indices,
   new workers the next numbers (before the function workers, which come
   last in every shipped script that has them; vanilla `call`s to those
   are renumbered), new entries the next entry numbers of their worker.
   All numbers are known before any code is placed, so code can request an
   entry or worker another part of the package adds.
3. **Place.** New code goes to the end of its worker's code. The patches on
   one vanilla method, from every mod, become one chain: the prefixes in
   load order, then the body, then the postfixes in load order, then `end`.
   The body is the last replacement, or with no replacement the vanilla
   code: a jump to it when there are only prefixes, else a copy of it in
   which every `end` jumps to the postfixes (exits through `endp`/`end_1`
   or jumps out of the method skip them, with a warning). The method's
   entries point at the chain; the vanilla code stays where it was.
4. **Lay out.** Every vanilla byte of the block stays at its offset: the
   data areas hold block offsets (the "other" area points into the shared
   data in 1,611 of 1,621 scripts), so moving them would break those. What
   grew goes after the block's end, aligned: the variable table and pools
   when they grew, worker headers (new ones, and vanilla ones that sit
   where the larger header table at `+0x38` needs room), entry and label
   tables that grew, the new variables' data (their offsets reach past the
   vanilla area from the storage's base), grown private data (below), and,
   when any code changed, the
   whole code region in worker order (`+0x30` points at it, `+0x00` is its
   length); the old code stays as unused bytes. Tables that kept their size
   are rewritten in place. A worker whose code no longer starts at an entry
   (its first method was patched) gets that head moved to its end, with a
   jump back, so that every worker starts at an entry as in the shipped
   scripts. The file's later sections shift when the script outgrows its
   section (`ScriptFile.ReplaceScript`).

**Private data.** New storage-3 variables (the hidden locals) form one
area, in load order. Storage 3 is resolved through the worker that runs
the code, also inside a function worker it calls
([script-block.md](script-block.md#private-data-at-run-time)), so every
worker that is not a function worker and whose code uses one of them (its
own, or a function worker's it calls, directly or not) gets the whole
area, at the same offset in each: past the longest vanilla private data
among those workers, rounded up to 4. Such a worker gets a new private
block after the block's end, `+0x10` bytes long (rounded up to 16): a
copy of its vanilla private bytes, then zeros; `+0x2c` points at it and
`+0x10` is the new length. The vanilla block stays where it was. A
vanilla worker without private data gets one. In battle the game sizes
each actor's private buffer from `+0x10` and clears it, so a grown worker
also gets the room there.

The cost: a linked script grows by its whole code region (about 1 KB for a
monster, up to tens of KB for an event) plus what the mods add. Laying the
code out in two runs, unchanged workers in place, needs `ScriptBlock` and
`ScriptImage` to accept a worker range that is not after the previous
one; left for later.

## Messages

A script holds one message index for all languages, so a new message gets
the same index in every language's table (`MessageLinker`, run by
`ModSet.Link` for a script whose packages have new messages):

1. **The first free index**: one past the last message of the longest of
   the script's tables, over every language (`new_<lang>pc/event/obj_ps3/
   <xx>/<event>/<event>.bin`, battles `new_<lang>pc/battle/btl/<name>/
   <name>.bin`) and, for an event, the `.ebp`'s own Japanese table
   (section 1), which the game reads where a language has none.
2. **Numbering**: the mods' new messages in load order, each package's in
   its order, from that index; the code's `newMessage` operands become
   those numbers. A script without any table (a monster: its `Text[n]` is
   the battle's) leaves such a package out, with an error.
3. **Tables**: every language that has a table gets a copy of it, grown to
   the last new index (entries between get an empty text and flags 0) with
   each new message encoded in that language's charset
   (`ffxsjistbl_<us|jp|kr|ch>.bin`). The mod's `source_language` gets the
   text; the other languages get a copy of it, listed in the report as
   untranslated (an info per mod and language). A character the charset
   lacks becomes its full-width form (the Japanese, Korean and Chinese
   charsets have no ASCII letters: `A` is written `Ａ`) or, failing that,
   `?` (a warning). A language without a table for the
   script gets nothing (a warning): the game would read past the `.ebp`'s
   table there.

The loader writes the tables to its `efl/x` next to the linked script,
where Fahrenheit serves them as it serves the scripts. A runtime injection
package's new messages are numbered the same way, after every mod's; while
the game runs they are set in the running event's table at once (with the
explicit ones), and its table files are rewritten when they were served at
startup.

## The report

Each message has a severity, the mod and a text: `info` (what was applied
and the numbers given), `warning` (a postfix some exits skip, a load order
`after` does not hold), `conflict` (two replacements of one method: the
last in load order wins; incompatible mods) and `error` (a hash mismatch,
a broken package, a missing requirement: not applied). Prefixes and
postfixes of several mods combine without a conflict.
