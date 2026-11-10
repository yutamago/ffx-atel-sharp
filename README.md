# Atel#

Decompiles the compiled ATEL scripts of Final Fantasy X (events, monster AI,
battle scripts) into standard C#, and compiles that C# back into the same
bytes. The C# then becomes the language of mods, which a
[Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) mod (the loader)
links onto the player's own scripts in the Steam HD Remaster.

The plan, its decisions and the phases are in [`docs/plan.md`](docs/plan.md).

## Docs

The website, for modders: <https://yutamago.github.io/ffx-atel-sharp/> (being written; the source is in [`site/`](site/)).

For modders, in reading order:

1. [`docs/tutorial.md`](docs/tutorial.md): a first mod, from decompiling to checking it in battle.
2. [`docs/language.md`](docs/language.md): the language reference, which C# is Atel.
3. [`docs/analyzers.md`](docs/analyzers.md): the rules that report C# ATEL cannot run.
4. [`docs/editor.md`](docs/editor.md): message text and the game's names in the editor.
5. [`docs/sdk.md`](docs/sdk.md): a mod as a C# project, built with `dotnet build`.
6. [`docs/formats/mod-package.md`](docs/formats/mod-package.md): what a mod ships and how the loader links it.

How it works:

- [`docs/encoding.md`](docs/encoding.md): how each construct compiles to bytecode, and what the
  compiler reproduces from the original compiler.
- [`docs/plan.md`](docs/plan.md): the plan, the decisions per phase and their numbers.
- [`docs/website.md`](docs/website.md): the plan for the GitHub Pages site.
- [`docs/corpus.md`](docs/corpus.md): the shipped scripts, measured; [`docs/survey.md`](docs/survey.md)
  is the generated survey behind it.
- [`docs/formats/script-block.md`](docs/formats/script-block.md): the script block, byte for byte.
- [`docs/research/loader.md`](docs/research/loader.md): the loader in the game, and the in-game checks.
- [`docs/research/dead-code.md`](docs/research/dead-code.md): the dead `if (false)` blocks, what they are, and names for them.
- [`docs/research/repeats.md`](docs/research/repeats.md): the code that repeats with only different values, as candidate inline helpers.
- [`docs/research/handlers.md`](docs/research/handlers.md): which worker and entry the battle runs for which event (turn, hit, death, cameras, motions), from FFX.exe.
- [`docs/research/frame-timing.md`](docs/research/frame-timing.md): what a script frame is (30 a second), waits in seconds, and why a frame-rate mod keeps the tick. The frame-rate mod that was `src/Ffx.Frames` (`yuta.frames`) is now FPS unlock (`yuta.fps-unlock`) in [ffx-mods](https://github.com/yutamago/ffx-mods) ([`docs/fps-unlock.md`](https://github.com/yutamago/ffx-mods/blob/main/docs/fps-unlock.md)).
- [`docs/research/oo-surface.md`](docs/research/oo-surface.md): the object-oriented members over the natives, how they were chosen and measured.
- [`docs/research/mod-saves.md`](docs/research/mod-saves.md): where a mod's saved fields live (the save's unused tail) and how the loader keeps them.
- [`docs/research/save-fields.md`](docs/research/save-fields.md): the save variables the `Save` class names.
- [`docs/source-repo.md`](docs/source-repo.md): the private repo of decompiled source.

## Status

Phases 0-4, 6, 7, 10 and 11 are done; 5 (names), 8 (tooling) and 9 (mods) are under way:

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
  and branch polarity. The 8,295 jumps that have no structured form stay
  `goto` (why, by shape: `atel roundtrip` and `docs/plan.md`, "The gotos
  that stay"); no `Asm` is left. `atel compile` turns that C# back into the
  script file. All 1,621 scripts round-trip byte for byte, and the source
  compiles as C# against `Ffx.Atel.Game` without errors or warnings.
  Workers that are identical across scripts share library base classes.
- Names (Phase 5, ongoing): story progress values as `Story` constants
  (`if (StoryProgress < Story.S01340_Kami0400)`), save variables after
  Fahrenheit's save fields (`Save`), enums for battle natives
  (`Btl.SetActorsProperty(Target.Own, ActorProperty.Hp, 0)`), messages as
  `Text[12]`, workers named after the model they load, and `Init`/`Main`
  overrides for the engine's entries 0 and 1; monster and battle workers and
  entries after the event the battle runs them for (`Ai.OnTurn`, `Ai.OnHit`,
  `Cameras.OnCamera24`; [`docs/research/handlers.md`](docs/research/handlers.md)).
- The source reads object-oriented: the natives most scripts call are members of the worker
  (`Pos = new Vector3(…)`, `Yaw = …`, `Turn.Start(…)`, `Motion.Play(…)`, `Tidus.Pos.X`) and of
  static classes, nested where the game groups them (`Wait.Frames(…)`, `Camera.Eye.Position = …`,
  `Camera.Focus.MoveEased(…)`, `Voice.Play(…)`), 74.5 % of all native calls, generated from
  `data/native-members.json` and compiled back onto the same natives ([`docs/language.md`](docs/language.md#members)).
- Repeated code is written once, as `[Inline]` methods: the reference assembly's (`Turn.To(…)`,
  `Move.To(…)`, `Dialogue.SayWithVoice(…)`, `ChangeMap(…)`, `Party.RestoreAll()`), and each worker's own,
  which the decompiler mines from its code (W36 of `bltz0000`: 190,402 lines → 15,447), still byte for
  byte ([`docs/language.md`](docs/language.md#repeated-code)).
- The analyzers report C# that ATEL cannot run (30 rules), in the IDE and
  in `atel compile --check`; see [`docs/analyzers.md`](docs/analyzers.md).
- Mods (Phase 9): `atel compile-mod` compiles a mod's C# (`partial`
  classes of the decompiled scripts, with `[Prefix]`, `[Postfix]`,
  `[Replace]`, new entries, workers and variables) into packages that hold
  no game code; `Ffx.Atel.Linker` links them onto the player's files, and
  the loader (`yuta.atel`) does that at game start for every Atel mod in
  Fahrenheit's load order. Also: new messages (`Text.New`), per-field data
  edits in a static constructor (`MonsterStats.Hp = 4321;`), `[Inject]` at the
  n-th call of a native inside a vanilla method, and `[Saved]` fields that a
  mod keeps per save slot (beside the save, never in it), readable by other
  mods. Each is checked in game. Not done yet:
  laying out unchanged workers in place (a linked file grows by 36-57 KB).
- The loader also serves rebuilt scripts from edited disassembly
  (`scripts/**/*.asm`), for development; see
  [`docs/research/loader.md`](docs/research/loader.md).
- The decompiled corpus lives in the private repo `yutamago/ffx-atel-src`:
  `atel export` writes it onto its `generated` branch, merges that into
  `main` (hand edits) and checks with `atel compile-all` that `main` still
  compiles to the game's bytes; see [`docs/source-repo.md`](docs/source-repo.md).
- The analyzers ([`docs/analyzers.md`](docs/analyzers.md)), the mod linker
  and the loader's link step ([`docs/formats/mod-package.md`](docs/formats/mod-package.md)).
- `AtelSharp.Sdk`: a mod is a C# project, `<Project Sdk="AtelSharp.Sdk">`
  (the version, one per commit of this repo such as `0.1.0-alpha.0.117.g5e8c870545`, in
  `global.json`), and `dotnet build` writes its Fahrenheit mod folder; see
  [`docs/sdk.md`](docs/sdk.md) and [`examples/LarvaHp`](examples/LarvaHp).
- The message text in the editor: `atel text export` decodes the player's
  message tables, per language, beside the decompiled source, and the VS
  Code extension in `editors/vscode` and the Rider plugin in `editors/rider`
  show each `Text[n]`'s message as an inlay hint and hover (Rider: quick
  documentation); `atel decompile --text-comments <lang>` writes it
  as comments instead; `atel names export` does the same for the game's
  names of monsters, commands and items, maps and battles, which both
  editors show after `Target.Biran`, `Command.Attack`, `Map.Hiku0800` and
  `Battle.Kami00_00`; see [`docs/editor.md`](docs/editor.md).
- Dead code: `atel dead` catalogues the 34,806 `if (false)` blocks (groups,
  live twins, signatures), and blocks we understand are named
  (`if (Cut.ZanarkandCrowdFacingReaction)`, still compiling to the shipped
  bytes); a mod switches a named block on with `[assembly:
  EnableCut(nameof(Cut.ZanarkandCrowdFacingReaction))]`, which the linker
  applies as a same-length no-op over the block's `jmp`; see
  [`docs/research/dead-code.md`](docs/research/dead-code.md). In game, the separate mod
  `yuta.atel.cuts` switches each name on and off from a menu (F8) in the loaded scripts
  ([`src/Ffx.Atel.Cuts/README.md`](src/Ffx.Atel.Cuts/README.md)).
- `Debug.Print(…)` and the other `Debug.*` natives write to the loader's log and to the screen (the last
  few lines top left, with the time and the worker, fading out; F6 shows or hides them, Shift+F6 changes
  how many: [`docs/formats/debug.md`](docs/formats/debug.md#on-screen)), and a package dropped into the
  loader's `inject/` folder while the game runs takes effect at the script's next load, its messages at
  once (Phase 10).

## Projects

| Project | Does |
|---|---|
| `Ffx.Atel.Bytecode` | VBF archives, script files, disassembler and assembler; FFX's text encoding, message and kernel tables |
| `Ffx.Atel.Game` | the reference assembly: the game's API for Atel source |
| `Ffx.Atel.Decompiler` | bytecode to C# |
| `Ffx.Atel.Compiler` | C# to bytecode, through Roslyn |
| `Ffx.Atel.Analyzers` | which C# is Atel: subset errors, warnings, code fixes |
| `Ffx.Atel.Linker` | applies compiled mod packages to vanilla scripts |
| `Ffx.Atel.Loader` | the Fahrenheit mod (`yuta.atel`) that links the enabled Atel mods and serves the results |
| `Ffx.Atel.Cuts` | the Fahrenheit mod (`yuta.atel.cuts`) that switches the named dead blocks (`Cut.*`) on and off in the loaded scripts, from a menu |
| `Ffx.Atel.Cli` | `atel`: list, disasm, asm, handlers, survey, decompile, compile, compile-all, roundtrip, export, text export, names export, compile-mod, link, dead, repeats |
| `AtelSharp.Sdk` | the MSBuild SDK package mod projects build with (`dotnet pack src/Ffx.Atel.Sdk`) |
| `editors/vscode` | the VS Code extension that shows message text and names (TypeScript; `npm test`, `npm run package`) |
| `editors/rider` | the Rider plugin that shows message text and names (ReSharper backend in C#, Gradle; `./gradlew check buildPlugin`) |

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
