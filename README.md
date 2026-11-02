# Atel#

Decompiles the compiled ATEL scripts of Final Fantasy X (events, monster AI,
battle scripts) into standard C#, and compiles that C# back into the same
bytes. The C# then becomes the language of mods, which a
[Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) mod (the loader)
links onto the player's own scripts in the Steam HD Remaster.

The plan, its decisions and the phases are in [`docs/plan.md`](docs/plan.md).

## Docs

For modders, in reading order:

1. [`docs/unity.md`](docs/unity.md): Atel for Unity people, how FFX's scripts read if you know C# from Unity.
2. [`docs/tutorial.md`](docs/tutorial.md): a first mod, from decompiling to checking it in battle.
3. [`docs/language.md`](docs/language.md): the language reference, which C# is Atel.
4. [`docs/analyzers.md`](docs/analyzers.md): the rules that report C# ATEL cannot run.
5. [`docs/editor.md`](docs/editor.md): message text in the editor.
6. [`docs/formats/mod-package.md`](docs/formats/mod-package.md): what a mod ships and how the loader links it.

How it works:

- [`docs/encoding.md`](docs/encoding.md): how each construct compiles to bytecode, and what the
  compiler reproduces from the original compiler.
- [`docs/plan.md`](docs/plan.md): the plan, the decisions per phase and their numbers.
- [`docs/corpus.md`](docs/corpus.md): the shipped scripts, measured; [`docs/survey.md`](docs/survey.md)
  is the generated survey behind it.
- [`docs/formats/script-block.md`](docs/formats/script-block.md): the script block, byte for byte.
- [`docs/research/loader.md`](docs/research/loader.md): the loader in the game, and the in-game checks.
- [`docs/source-repo.md`](docs/source-repo.md): the private repo of decompiled source.

## Status

Phases 0-4, 6 and 7 are done; 5, 8 and 9 are under way:

- `Ffx.Atel.Bytecode` reads `FFX_Data.vbf`, the event, monster and battle
  files and their script blocks, and disassembles and reassembles them.
  All 1,621 scripts come back byte for byte, and the disassembly is the
  same text as ffx-rust's `ffx script disasm`.
- `atel decompile` writes a script as C#, a folder per script and a file
  per worker: its declarations as attributes and fields, and every entry's
  code as C# statements (assignments, native calls, requests as
  `_ = Lulu.E5Async();` and `await`, `return;`), structured as
  `if`/`else`, `while`, `do`, `for` and `switch` with `case` patterns,
  `break`, `continue` and `goto case`, with the original's label numbers
  and branch polarity. The 8,448 jumps that have no structured form stay
  `goto`; no `Asm` is left. `atel compile` turns that C# back into the
  script file. All 1,621 scripts round-trip byte for byte, and the source
  compiles as C# against `Ffx.Atel.Game` without errors or warnings.
  Workers that are identical across scripts share library base classes.
- Names (Phase 5, ongoing): story progress values as `Story` constants
  (`if (StoryProgress < Story.S01340_Kami0400)`), save variables after
  Fahrenheit's save fields (`Save`), enums for battle natives
  (`Btl.SetActorsProperty(Target.Own, ActorProperty.Hp, 0)`), messages as
  `Text[12]`, workers named after the model they load, and `Init`/`Main`
  overrides for the engine's entries 0 and 1.
- The analyzers report C# that ATEL cannot run (21 rules), in the IDE and
  in `atel compile --check`; see [`docs/analyzers.md`](docs/analyzers.md).
- Mods (Phase 9): `atel compile-mod` compiles a mod's C# (`partial`
  classes of the decompiled scripts, with `[Prefix]`, `[Postfix]`,
  `[Replace]`, new entries, workers and variables) into packages that hold
  no game code; `Ffx.Atel.Linker` links them onto the player's files, and
  the loader (`yuta.atel`) does that at game start for every Atel mod in
  Fahrenheit's load order. Checked in game with the mod of
  [`docs/tutorial.md`](docs/tutorial.md). Not done yet: `Text.New`,
  per-field data edits, `[Inject]`.
- The loader also serves rebuilt scripts from edited disassembly
  (`scripts/**/*.asm`), for development; see
  [`docs/research/loader.md`](docs/research/loader.md).
- The decompiled corpus lives in the private repo `yutamago/ffx-atel-src`:
  `atel export` writes it onto its `generated` branch, merges that into
  `main` (hand edits) and checks with `atel compile-all` that `main` still
  compiles to the game's bytes; see [`docs/source-repo.md`](docs/source-repo.md).
- The analyzers ([`docs/analyzers.md`](docs/analyzers.md)), the mod linker
  and the loader's link step ([`docs/formats/mod-package.md`](docs/formats/mod-package.md)).
- `Ffx.Atel.Sdk`: a mod is a C# project, `<Project Sdk="Ffx.Atel.Sdk/0.1.0-alpha">`,
  and `dotnet build` writes its Fahrenheit mod folder; see
  [`docs/sdk.md`](docs/sdk.md) and [`examples/LarvaHp`](examples/LarvaHp).
- The message text in the editor: `atel text export` decodes the player's
  message tables, per language, beside the decompiled source, and the VS
  Code extension in `editors/vscode` and the Rider plugin in `editors/rider`
  show each `Text[n]`'s message as an inlay hint and hover (Rider: quick
  documentation); `atel decompile --text-comments <lang>` writes it
  as comments instead; see [`docs/editor.md`](docs/editor.md).
- Not done: `Debug.*` text and runtime packages (Phase 10).

## Projects

| Project | Does |
|---|---|
| `Ffx.Atel.Bytecode` | VBF archives, script files, disassembler and assembler; FFX's text encoding and message tables |
| `Ffx.Atel.Game` | the reference assembly: the game's API for Atel source |
| `Ffx.Atel.Decompiler` | bytecode to C# |
| `Ffx.Atel.Compiler` | C# to bytecode, through Roslyn |
| `Ffx.Atel.Analyzers` | which C# is Atel: subset errors, warnings, code fixes |
| `Ffx.Atel.Linker` | applies compiled mod packages to vanilla scripts |
| `Ffx.Atel.Loader` | the Fahrenheit mod (`yuta.atel`) that links the enabled Atel mods and serves the results |
| `Ffx.Atel.Cli` | `atel`: list, disasm, asm, survey, decompile, compile, compile-all, roundtrip, export, text export, compile-mod, link |
| `Ffx.Atel.Sdk` | the MSBuild SDK package mod projects build with (`dotnet pack src/Ffx.Atel.Sdk`) |
| `editors/vscode` | the VS Code extension that shows message text (TypeScript; `npm test`, `npm run package`) |
| `editors/rider` | the Rider plugin that shows message text (ReSharper backend in C#, Gradle; `./gradlew check buildPlugin`) |

## Building

.NET 10:

```bash
dotnet build ffx-atel-sharp.slnx
dotnet test ffx-atel-sharp.slnx
```

The corpus tests need your copy of the game and skip without it:

```bash
export FFX_GAME_DIR=<the folder with FFX.exe>
export FFX_SCRIPT_CLI=<ffx-rust>/target/release/ffx   # optional: cross-check with ffx-rust
dotnet test -c Release ffx-atel-sharp.slnx
artifacts/bin/Ffx.Atel.Cli/release/atel disasm m001
artifacts/bin/Ffx.Atel.Cli/release/atel decompile m001 -o <dir>         # <dir>/Monsters/M001/M001.cs
artifacts/bin/Ffx.Atel.Cli/release/atel compile <dir>/Monsters/M001 -o m001.bin
artifacts/bin/Ffx.Atel.Cli/release/atel roundtrip --check               # every script
```

To write a mod and run it in the game, follow
[`docs/tutorial.md`](docs/tutorial.md). The loader also rebuilds scripts
from edited disassembly, for development: build it (`dotnet build -c Release
src/Ffx.Atel.Loader`), copy `artifacts/deploy/release/mods/yuta.atel` into
your Fahrenheit folder's `mods/`, add `yuta.atel` to `mods/loadorder`, and
put an edited disassembly into `mods/yuta.atel/scripts/`:

```bash
atel disasm m209 -o m209.asm      # first line: "; <archive path>"; keep it
# edit m209.asm, then copy it to <Fahrenheit>/mods/yuta.atel/scripts/
```

At start the loader writes the rebuilt and linked files to
`mods/yuta.atel/efl/x/` (it owns that folder) and logs what it built.

To make a mod, see [`docs/sdk.md`](docs/sdk.md).

No game files, extracted scripts or decompiled source go into this
repository.

## License

MIT, see [`LICENSE`](LICENSE). The bytecode layer is a port of the
`ffx-vbf` and `ffx-script` crates of ffx-rust (MIT OR Apache-2.0, same
author).
