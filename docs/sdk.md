<!-- SPDX-License-Identifier: MIT -->
# Making a mod: Ffx.Atel.Sdk

An Atel mod is a C# project. `dotnet build` checks it against the game's
decompiled source and writes a Fahrenheit mod folder. That folder holds only
the mod's own code, as packages the loader links onto the player's scripts
(plan: "Mods"; format: [formats/mod-package.md](formats/mod-package.md)).
The examples are [`examples/LarvaHp`](../examples/LarvaHp) (a postfix),
[`examples/ToughLarva`](../examples/ToughLarva) (data edits) and
[`examples/LarvaInject`](../examples/LarvaInject) (`[Inject]`).

## What you need

- .NET 10 SDK.
- Your copy of the game (Steam HD Remaster), with `FFX_GAME_DIR` set to its
  folder, the one with `FFX.exe`.
- [Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) and the Atel
  loader (`yuta.atel`) to play the mod.
- The `Ffx.Atel.Sdk` package from a NuGet source. It is not published yet.
  Until it is, pack it from a checkout of this repo and add a local source:

  ```bash
  dotnet pack src/Ffx.Atel.Sdk -c Release -o ~/atel-feed
  dotnet nuget add source ~/atel-feed --name atel
  ```

  The package's version is the commit's (see "Versions" below), for example
  `Ffx.Atel.Sdk.0.1.0-alpha.0.117.g5e8c870545.nupkg`. Pin that version in
  your mod (step 2).

The package also carries the `atel` tool (`tools/atel/atel.dll`):
`dotnet ~/.nuget/packages/ffx.atel.sdk/<version>/tools/atel/atel.dll --help`.

### Versions

NuGet caches a package by version in `~/.nuget/packages/` and never reads
that version again. So every commit of this repo builds its own version, and
everything built from one commit has the same one:

```
0.1.0-alpha.0.<commits>.g<commit>
```

`<commits>` is the number of commits in the history of the commit built
(`git rev-list --count HEAD`), so a later commit always has a higher version;
`<commit>` is the commit's first 10 hex digits, which tells apart two branches
with the same count. `Directory.Build.targets` computes it with git on every
build, so a checkout must be a git clone with its whole history: a shallow
clone stops the build (`git fetch --unshallow`; in CI, `actions/checkout`
with `fetch-depth: 0`). `-p:Version=<version>` replaces it (the release,
"Publishing" below). The `atel` CLI carries the same version: `atel
decompile` pins the SDK of its own version in `global.json` and looks for
that package.

Uncommitted changes do not change the version: the build of a commit is
reproducible, so a dirty checkout packs under the commit's version. When you
pack a changed SDK without committing, delete
`~/.nuget/packages/ffx.atel.sdk/<version>/` (and the copy in a decompiled
tree's `.atel/packages/`), or commit and pack again.

## 1. Decompile the vanilla source

A mod changes scripts by adding to their decompiled source, so it needs that
source. Decompile the scripts the mod changes (or all of them), **outside the
mod's folder**:

```bash
atel decompile m209 -o ~/atel-vanilla          # or: atel decompile all -o ~/atel-vanilla
```

A clone of the private source repo (`ffx-atel-src`) works as well. Nothing of
it goes into the mod: it is the game's code.

`atel decompile -o` writes projects that use the SDK, so the vanilla source
opens in the IDE with the analyzers. The projects are
`<Project Sdk="Ffx.Atel.Sdk">`, with the SDK's version in `global.json` (the
version of the `atel` that wrote them, "Versions" above). When `atel` finds the package beside
itself (run from the package, or from a checkout after `dotnet pack
src/Ffx.Atel.Sdk`), or is given it with `--sdk <dir or .nupkg>`, it copies the
package to `.atel/packages/` and writes a `nuget.config` naming that folder,
so the tree builds with no other source. `--no-sdk` writes the projects of
Phase 6 instead: plain `Microsoft.NET.Sdk` projects that reference
`.atel/Ffx.Atel.Game.dll` (or `-p:AtelGameProject=<Ffx.Atel.Game.csproj>`),
without the analyzers. `dotnet build` of a vanilla project only checks the C#.

## 2. Create the project

```xml
<Project Sdk="Ffx.Atel.Sdk">
    <PropertyGroup>
        <AtelModId>yuta.larvahp</AtelModId>
        <AtelModName>Larva HP</AtelModName>
        <Description>The Larva (m209) starts with 1234 HP.</Description>
        <Authors>you</Authors>
        <Version>0.1.0</Version>
        <AtelVanillaSource>../atel-vanilla</AtelVanillaSource>
    </PropertyGroup>
</Project>
```

The SDK's version goes in a `global.json` next to the project or in a folder
above it, the version of the package you packed or installed:

```json
{ "msbuild-sdks": { "Ffx.Atel.Sdk": "0.1.0-alpha.0.117.g5e8c870545" } }
```

(or in the project: `<Project Sdk="Ffx.Atel.Sdk/0.1.0-alpha.0.117.g5e8c870545">`).
A new SDK is a new version: change it here to update.

A `.cs` file per script you change, in that script's namespace:

```csharp
using Ffx;
using Ffx.Atel;

namespace Monsters.M209;

public sealed partial class Ai
{
    [Postfix(nameof(Init))]
    void MoreHp()
    {
        Btl.SetActorProperty(ActorProperty.Hp, 1234);
    }
}
```

The namespace says which script a file changes: `Monsters.M209` is
`<vanilla>/Monsters/M209/`. What a mod can write (`partial` classes of the
vanilla workers and the script class, `[Prefix]`, `[Postfix]`, `[Replace]`, new
entries, workers and variables) is in the plan, "Phase 9: mods".
`mod.toml` next to the project (optional) holds `after`, `requires` and
`incompatible`.

### Switching dead code on

The decompiled source names some of the code the game never runs:
`if (Cut.ZanarkandCrowdFacingReaction) { … }` is `if (false)`, and each
constant of `Ffx.Cut` says in its doc comment what the block does
([research/dead-code.md](research/dead-code.md)). A mod runs such a block
with an assembly attribute, in any of its files:

```csharp
using Ffx;
using Ffx.Atel;

[assembly: EnableCut(nameof(Cut.ZanarkandCrowdFacingReaction))]
```

A file with only these lines needs no namespace and no vanilla source. The
build puts the switch into the package of every script that has the block
(`znkd1300` here), and the loader turns the block's `jmp` into a no-op of the
same length in your copy of the script, so the block runs as if it were
`if (true)` and nothing else in the script changes. If your copy does not
have the block (another game version), the build stops; if a file the loader
links does not, that script's package is left out with an error in the
report. Several mods may enable the same name. A block may change the save
(`HighroadBattleLine335Tidus` marks its line as seen), so read the doc
comment first.

### Properties

| Property | Default | Meaning |
|---|---|---|
| `AtelVanillaSource` | (required) | the vanilla source root: what `atel decompile -o` wrote, or a clone of the source repo. Relative to the project or absolute. Set it in the project, a `Directory.Build.props`, the environment, or `-p:AtelVanillaSource=…` |
| `FFX_GAME_DIR` or `AtelGameDir` | the environment | the game folder: the package names each script by its path in the game's archive |
| `AtelModId` | the project name | the Fahrenheit mod id: the folder name and the manifest's `Id` |
| `AtelModName`, `Description`, `Authors`, `Version`, `RepositoryUrl` | | the manifest's `Name`, `Desc`, `Authors`, `Version`, `Link` |
| `AtelModOutputDir` | `bin/<configuration>/net10.0/mods/<id>/` | where the mod folder goes |
| `AtelWriteManifest` | `true` | `false`: no `<id>.manifest.json` (the folder belongs to another Fahrenheit mod, see below) |
| `AtelModDependencies` | `yuta.atel` | the manifest's `Dependencies` (`;`-separated) |
| `AtelWritePackage` | `true` | `false`: check the C# only (no game needed) |
| `AtelProjectKind` | `Mod` | `Vanilla` for decompiled source (`atel decompile` sets it) |

The SDK sets what Atel needs and you may override: `net10.0` (for the IDE;
nothing runs on .NET), no implicit usings, nullable on, CS0162 and CS1998 off.
It references `Ffx.Atel.Game` (with its docs) and the analyzers and code
fixes, which run in the IDE and on build ([analyzers.md](analyzers.md)).

## 3. Build

```bash
export FFX_GAME_DIR=~/steam-games/FFX-X2-HD
dotnet build
```

What happens:

1. Before the C# compile, the SDK adds the vanilla source of every script the
   mod's files name (by namespace), and the vanilla `Library/`, to the
   compilation. Your `partial` classes compile together with the decompiled
   ones, so the IDE knows `Init`, the vanilla variables and workers. The
   vanilla files count as generated code: the analyzers report only on your
   files. This also runs in the IDE's design-time builds.
2. After the build, `atel compile-mod` (from the package, run with `dotnet`)
   compiles the mod against the vanilla source and writes
   `bin/Debug/net10.0/mods/<id>/`:

   ```
   mods/yuta.larvahp/
     yuta.larvahp.manifest.json      Fahrenheit's manifest, depends on yuta.atel
     mod.toml                        when the project has one
     atel/ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin.json
   ```

   With `[assembly: EnableCut(…)]`, the packages of the scripts the named
   blocks are in carry them too (`"cuts"`, format 2).

The compiled DLL is only the C# check. It contains the vanilla code, so it
stays in `obj/` and is copied nowhere. Without `FFX_GAME_DIR` or
`AtelVanillaSource` the build stops with ATLSDK004 or ATLSDK001. A namespace
with no vanilla folder gives ATLSDK003.

## 4. Install

Copy `bin/Debug/net10.0/mods/` into your Fahrenheit folder (it merges into
`mods/<id>/`) and add the id to `mods/loadorder`, where `yuta.atel` must be
listed too. At start
the loader links every enabled Atel mod onto your own files, in load order, and
logs what it applied ([formats/mod-package.md](formats/mod-package.md), "The
report").

## With a Fahrenheit code mod

An Atel mod project must not also be a Fahrenheit DLL mod (`Fahrenheit.Sdk`).
Its compilation holds the vanilla source, and that DLL would ship the game's
code. Fahrenheit.Sdk also builds for `win-x86`, publishes after every build, and
writes its own manifest with no dependencies.

Use two projects instead:

- **Two mods** (simplest): the Fahrenheit project is one mod and the Atel
  project another (`<id>.atel`). Each has its own folder and manifest.
- **One mod**: the Atel project writes its packages into the Fahrenheit mod's
  folder, without a manifest of its own:

  ```xml
  <AtelModOutputDir>../MyMod/bin/$(Configuration)/net10.0/win-x86/publish/</AtelModOutputDir>
  <AtelWriteManifest>false</AtelWriteManifest>
  ```

  Build the Fahrenheit project first. The loader takes every mod in the load
  order that has an `atel/` folder as an Atel mod, DLL or not. Fahrenheit's
  manifest does not list `yuta.atel` then, so Fahrenheit does not check that
  the loader is installed: list it in `mods/loadorder` yourself.

## Why an MSBuild project SDK

The alternative is a `PackageReference` with `build/` props and targets. A
package is restored only after the project has a target framework, so it can
set neither the framework nor anything read before restore. An SDK runs before
the project body, as `Microsoft.NET.Sdk` and `Fahrenheit.Sdk` do. A mod project
is then a single line plus its properties, and the decompiled projects are one
line each.

The compiler runs as the `atel` CLI (`dotnet atel.dll compile-mod`), not as an
MSBuild task. Visual Studio's MSBuild runs on .NET Framework, which cannot load
the .NET 10 compiler. The CLI is a separate process, so the compiler's Roslyn
never meets the one MSBuild loads, and the memory of reading the game's
archive is freed when the process ends. The tool is framework-dependent, so
the same package works on Windows, Linux and macOS.

## Not yet

- The package is not published yet; CI only packs it. Publishing is prepared (below) and waits
  for a decision.
- A `dotnet new` template; for now copy `examples/LarvaHp`.
- `atel compile-mod` reads every `.cs` under the project folder (but `obj/`
  and `bin/`), not the project's `Compile` items.

## Publishing

The plan is nuget.org (the id `Ffx.Atel.Sdk` was free on 2026-11-02). `.github/workflows/release-sdk.yml`
publishes when a tag `sdk-v<version>` is pushed (`git tag sdk-v0.1.0-alpha.1 && git push origin
sdk-v0.1.0-alpha.1`): it packs that version (`-p:Version=`, instead of the commit's), checks the package
for the checkout path and pushes it with the repository secret `NUGET_API_KEY` (a nuget.org API key
limited to pushing `Ffx.Atel.Sdk`). Nothing is published before both exist. Before the first push:

- A nuget.org package is public and permanent (a version can be unlisted, not deleted). The package
  holds the reference assembly, whose enums carry names from Fahrenheit's ID tables (used with
  permission from their lead for this project); check that the permission covers a public package.
- The package's project URL points at this repository, which is private.
- NuGet orders `0.1.0-alpha` below every commit's `0.1.0-alpha.0.<commits>…`. A release must sort above
  the builds before it: `0.1.0-alpha.1`, then `0.1.0-alpha.2`, and so on. Never tag a version twice.
