<!-- SPDX-License-Identifier: MIT -->
# Changelog

The released packages of Atel#: `Ffx.Atel.Sdk` (the MSBuild project SDK, with the `atel` tool, the game's
reference assembly and the analyzers) and `Ffx.Atel.Templates` (`dotnet new atel-mod`). The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions are [SemVer 2](https://semver.org/).
A release is a tag `v<version>` on `main` with a section `## [<version>]` here ([docs/release.md](docs/release.md)).

Builds between releases have their commit's version (`0.1.0-alpha.0.<commits>.g<commit>`) and are not released.

## [Unreleased]

What the first release would hold.

### Added

- `Ffx.Atel.Sdk`: `<Project Sdk="Ffx.Atel.Sdk">` mod projects. `dotnet build` checks the mod against the decompiled
  vanilla source and writes the Fahrenheit mod folder, one package per script the mod changes
  ([docs/sdk.md](docs/sdk.md)).
- The `atel` tool in the SDK package (`tools/atel/atel.dll`): `decompile`, `compile`, `compile-mod`, `link`,
  `text export`, `names export` and the rest ([docs/tutorial.md](docs/tutorial.md)).
- The game's reference assembly `Ffx.Atel.Game` with its docs, the analyzers and code fixes
  ([docs/analyzers.md](docs/analyzers.md)).
- Mods: prefixes, postfixes, replacements, `[Inject]`, new entries, workers and variables, data edits, saved fields,
  `Text.New`, `[assembly: EnableCut]` ([docs/language.md](docs/language.md#mods)).
- `Ffx.Atel.Templates`: `dotnet new atel-mod -n MyMod --script m209`, a mod project with `global.json`,
  `nuget.config` and a README.
