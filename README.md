# Atel#

Decompiles the compiled ATEL scripts of Final Fantasy X (events, monster AI,
battle scripts) into standard C#, and compiles that C# back into the same
bytes. The C# then becomes the language of mods, which a
[Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) mod (the loader)
links onto the player's own scripts in the Steam HD Remaster.

The plan, the language and the phases are in [`docs/plan.md`](docs/plan.md).

## Status

Phase 4 of the plan, control flow (Phases 0-3 done):

- `Ffx.Atel.Bytecode` reads `FFX_Data.vbf`, the event, monster and battle
  files and their script blocks, and disassembles and reassembles them.
  All 1,621 scripts come back byte for byte, and the disassembly is the
  same text as ffx-rust's `ffx script disasm`.
- `atel decompile` writes a script as C#: its declarations as attributes
  and fields, and every entry's code as C# statements (assignments, native
  calls such as `Chr.PlayMotion(…)`, requests, `return;`), structured as
  `if`/`else`, `while`, `do`, `for` and `switch` with `case` patterns,
  `break`, `continue` and `goto case`, with the original's label numbers
  and branch polarity. The 8,448 jumps that have no structured form stay
  `goto`; no `Asm` is left. `atel compile` turns that C# back into the
  script file. All 1,621 scripts round-trip byte for byte, and the source
  compiles as C# against `Ffx.Atel.Game` without errors or warnings. The
  shape is in [`docs/plan.md`](docs/plan.md), "Phase 2: the source shape",
  "Phase 3: statements" and "Phase 4: control flow".
- `Ffx.Atel.Loader` (`yuta.atel`) gives the game rebuilt scripts: it
  assembles each `scripts/**/*.asm` in its mod folder (edited `atel disasm`
  output) onto the player's own file and serves the result through
  Fahrenheit's file loader. Checked in game, including scripts that grow;
  see [`docs/research/loader.md`](docs/research/loader.md).
- The analyzers, linker and SDK are empty so far.

## Projects

| Project | Does |
|---|---|
| `Ffx.Atel.Bytecode` | VBF archives, script files, disassembler and assembler |
| `Ffx.Atel.Game` | the reference assembly: the game's API for Atel source |
| `Ffx.Atel.Decompiler` | bytecode to C# |
| `Ffx.Atel.Compiler` | C# to bytecode, through Roslyn |
| `Ffx.Atel.Analyzers` | which C# is Atel: subset errors, warnings, code fixes |
| `Ffx.Atel.Linker` | applies compiled mod packages to vanilla scripts |
| `Ffx.Atel.Loader` | the Fahrenheit mod (`yuta.atel`) that serves the linked scripts |
| `Ffx.Atel.Cli` | `atel`: list, disasm, asm, decompile, compile, roundtrip; later link |
| `Ffx.Atel.Sdk` | the NuGet package mod projects build with |

## Building

.NET 10:

```bash
dotnet build ffx-atel-sharp.slnx
dotnet test ffx-atel-sharp.slnx
```

The corpus tests need your copy of the game and skip without it:

```bash
export FFX_GAME_DIR=~/steam-games/FFX-X2-HD          # the folder with FFX.exe
export FFX_SCRIPT_CLI=<ffx-rust>/target/release/ffx   # optional: cross-check with ffx-rust
dotnet test -c Release ffx-atel-sharp.slnx
artifacts/bin/Ffx.Atel.Cli/release/atel disasm m001
artifacts/bin/Ffx.Atel.Cli/release/atel decompile m001 -o <dir>         # <dir>/Monsters/M001/M001.cs
artifacts/bin/Ffx.Atel.Cli/release/atel compile <dir>/Monsters/M001 -o m001.bin
artifacts/bin/Ffx.Atel.Cli/release/atel roundtrip --check               # every script
```

To try the loader, build it (`dotnet build -c Release
src/Ffx.Atel.Loader`), copy `artifacts/deploy/release/mods/yuta.atel` into
your Fahrenheit folder's `mods/`, add `yuta.atel` to `mods/loadorder`, and
put an edited disassembly into `mods/yuta.atel/scripts/`:

```bash
atel disasm m209 -o m209.asm      # first line: "; <archive path>"; keep it
# edit m209.asm, then copy it to <Fahrenheit>/mods/yuta.atel/scripts/
```

At start the loader writes the rebuilt files to `mods/yuta.atel/efl/x/`
(it owns that folder) and logs what it built.

No game files, extracted scripts or decompiled source go into this
repository.

## License

MIT, see [`LICENSE`](LICENSE). The bytecode layer is a port of the
`ffx-vbf` and `ffx-script` crates of ffx-rust (MIT OR Apache-2.0, same
author).
