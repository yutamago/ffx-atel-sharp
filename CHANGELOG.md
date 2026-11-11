<!-- SPDX-License-Identifier: MIT -->
# Changelog

The released packages of Atel#: `AtelSharp.Sdk` (the MSBuild project SDK, with the `atel` tool, the game's
reference assembly and the analyzers) and `AtelSharp.Templates` (`dotnet new atel-mod`). The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions are [SemVer 2](https://semver.org/).
A release is a tag `v<version>` on `main` with a section `## [<version>]` here ([docs/release.md](docs/release.md)).

Builds between releases have their commit's version (`0.1.0-alpha.0.<commits>.g<commit>`) and are not released.

## [Unreleased]

### Changed

- Each analyzer's help link (the link on the diagnostic in the IDE) opens its page on the website,
  `https://yutamago.github.io/ffx-atel-sharp/reference/analyzers/atlNNNN/`, instead of a section of
  `docs/analyzers.md`.

### Fixed

- ATL0001 (a register used across a wait while another entry writes it) checks `static` register fields, the form
  the decompiler writes; before, it saw only instance fields and never fired on decompiled or new code.

## [0.1.0-alpha.1] - 2026-11-10

The first release: a preview for modders who want to try Atel. Atel mods change the event, battle and monster
scripts of Final Fantasy X (the Steam HD Remaster) in C#, compiled to the game's ATEL bytecode and loaded through
[Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) and the Atel loader (`yuta.atel`). It needs .NET 10 and
your own copy of the game; the loader is built from the repository for now ([docs/tutorial.md](https://github.com/yutamago/ffx-atel-sharp/blob/v0.1.0-alpha.1/docs/tutorial.md)).

### Added

- `AtelSharp.Sdk`, an MSBuild project SDK: a mod is `<Project Sdk="AtelSharp.Sdk">` with the version in
  `global.json`. `dotnet build` checks the mod against the decompiled vanilla source and writes the Fahrenheit mod
  folder, its manifest and one package per script the mod changes ([docs/sdk.md](https://github.com/yutamago/ffx-atel-sharp/blob/v0.1.0-alpha.1/docs/sdk.md)).
- The game's reference assembly `Ffx.Atel.Game` with its docs (the natives, the save fields, the enums with
  Fahrenheit's ID-table names, used with the Fahrenheit project's permission), and the analyzers and code fixes
  that say which C# is Atel, in the IDE and on build ([docs/analyzers.md](https://github.com/yutamago/ffx-atel-sharp/blob/v0.1.0-alpha.1/docs/analyzers.md)).
- The `atel` tool in the SDK package (`tools/atel/atel.dll`, run with `dotnet`): `decompile` (one script or all
  1,621, into projects of this SDK), `compile`, `compile-mod`, `link`, `text export`, `names export`, `dead` and the
  rest ([docs/tutorial.md](https://github.com/yutamago/ffx-atel-sharp/blob/v0.1.0-alpha.1/docs/tutorial.md)).
- Mods: prefixes, postfixes and replacements of vanilla methods, `[Inject]` at a native call, new entries, workers
  and variables, data edits, saved fields other mods can read, `Text.New`, and named dead code switched on with
  `[assembly: EnableCut]` ([docs/language.md](https://github.com/yutamago/ffx-atel-sharp/blob/v0.1.0-alpha.1/docs/language.md#mods)).
- `AtelSharp.Templates`: `dotnet new install AtelSharp.Templates`, then `dotnet new atel-mod -n MyMod --script m209`
  writes a mod project for one script (its namespace, class and folder as `atel decompile` names them), with
  `global.json` pinning the SDK of the same version, a README on decompiling, building and installing, and an MIT
  `LICENSE` with SPDX headers (`--authors` for the copyright holder, `--license none` for none).
- On the GitHub release, not on NuGet: the VS Code extension (`.vsix`) and the Rider plugin (`.zip`), version
  0.1.0, which show the message text and the game's names in the decompiled source
  ([docs/editor.md](https://github.com/yutamago/ffx-atel-sharp/blob/v0.1.0-alpha.1/docs/editor.md)).
