# atel-mod-name

An [Atel](https://github.com/yutamago/ffx-atel-sharp) mod for Final Fantasy X (the Steam HD Remaster),
loaded through [Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) and the Atel loader
(`yuta.atel`). It changes `atel-script-name`, whose source is in the namespace `AtelScriptNamespace`.

## What you need

- the .NET 10 SDK;
- your own copy of the game, with `FFX_GAME_DIR` set to its folder (the one with `FFX.exe`);
- the `Ffx.Atel.Sdk` package, version `atel-sdk-version` (`global.json`).

### The SDK package

The package is not on nuget.org yet. Until it is, `nuget.config` names a local folder,
`atel-feed-dir` (relative to this project), as a package source. Pack the SDK into it from a clone of
ffx-atel-sharp at the commit of that version:

```bash
dotnet pack src/Ffx.Atel.Sdk -c Release -o <this project>/atel-feed-dir
```

The package's file is `Ffx.Atel.Sdk.atel-sdk-version.nupkg`. Another version of the SDK is another
`global.json`; NuGet caches a version once and never reads it again.

## 1. Decompile the vanilla source

The mod adds to the decompiled source of the script it changes, so it needs that source. Decompile it
**outside this folder**, with the `atel` tool the SDK package carries:

```bash
dotnet restore
dotnet ~/.nuget/packages/ffx.atel.sdk/atel-sdk-version/tools/atel/atel.dll decompile atel-script-name -o atel-vanilla-dir
```

`dotnet restore` puts the SDK package into NuGet's package folder (`~/.nuget/packages/`, or
`NUGET_PACKAGES`). `atel decompile all -o <dir>` decompiles every script. A clone of the source repository
works as well. None of it goes into this mod: it is the game's code.

`AtelVanillaSource` in `ModTemplateName.csproj` points at that folder (`atel-vanilla-dir`). Open the vanilla
`Atel.slnx` in your IDE to read the script: `AtelScriptFolder/` holds its workers and methods.

## 2. Write the mod

`AtelScriptClass.cs` changes `atel-script-name`: its namespace, `AtelScriptNamespace`, names the script. It
adds a variable to the script class and shows how a patch on a vanilla worker looks. A mod can add
variables, entries and workers, and run code before, after, instead of or inside the vanilla methods;
see `docs/language.md` ("Mods") and `docs/sdk.md` in ffx-atel-sharp. Another script is another `.cs` file
in that script's namespace (the folder of its source, with dots).

## 3. Build

```bash
export FFX_GAME_DIR=<the game folder>
dotnet build
```

The build checks the C# against the vanilla source (the analyzers also run in the IDE) and writes the
Fahrenheit mod folder `bin/Debug/net10.0/mods/atel-mod-id/`: the manifest, `mod.toml`, and one package
per script the mod changes under `atel/`.

## 4. Install

Copy `bin/Debug/net10.0/mods/` into your Fahrenheit folder and add `atel-mod-id` to `mods/loadorder`,
after `yuta.atel`. The loader links the mod onto your own files when the game starts.
