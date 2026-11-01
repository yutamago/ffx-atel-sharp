# Atel#

Decompiles the compiled ATEL scripts of Final Fantasy X (events, monster AI,
battle scripts) into standard C#, and compiles that C# back into the same
bytes. The C# then becomes the language of mods, which a
[Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) mod (the loader)
links onto the player's own scripts in the Steam HD Remaster.

The plan, the language and the phases are in [`docs/plan.md`](docs/plan.md).

## Status

Phase 2 of the plan has started: the bytecode layer.

- `Ffx.Atel.Bytecode` reads `FFX_Data.vbf`, the event and monster files and
  their script blocks, and disassembles and reassembles them. All 758
  scripts (17.3 million instructions) come back byte for byte, and the
  disassembly is the same text as ffx-rust's `ffx script disasm`.
- The other projects are empty so far.

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
| `Ffx.Atel.Cli` | `atel`: list, disasm, asm; later decompile, compile, link, roundtrip |
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
artifacts/bin/Ffx.Atel.Cli/debug/atel disasm m001
```

No game files, extracted scripts or decompiled source go into this
repository.

## License

MIT, see [`LICENSE`](LICENSE). The bytecode layer is a port of the
`ffx-vbf` and `ffx-script` crates of ffx-rust (MIT OR Apache-2.0, same
author).
