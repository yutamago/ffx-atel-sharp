---
title: "4. Build the mod"
description: What dotnet build checks, and the mod folder it writes.
sidebar:
  order: 4
sources:
  - "docs/sdk.md#3-build"
  - "docs/tutorial.md#5-build-the-mod-package"
  - "docs/formats/mod-package.md#a-mod-on-disk"
---

In the `ToughDingo` folder, with `FFX_GAME_DIR` set:

```sh
dotnet build
```

The build does two things.

First it compiles your mod together with the Dingo's vanilla source, as ordinary C#, with the Atel# analyzers on. C# errors and Atel# errors stop the build here, the same ones your editor shows. The analyzers know which C# the game's script machine can run. A `string` local, for example, is an error, because the machine has nowhere to keep one.

Then it compiles your mod to ATEL bytecode and writes the mod folder:

```
bin/Debug/net10.0/mods/your-name.toughdingo/
  your-name.toughdingo.manifest.json
  mod.toml
  atel/…/m009.bin.json
```

- The manifest is Fahrenheit's description of the mod. It lists `yuta.atel` as a dependency, so Fahrenheit refuses to load the mod without the loader.
- The file under `atel/` is the package for `m009`. It holds your three changes and a fingerprint of the vanilla data they apply to. It holds nothing from the game's files.

The DLL the build also makes contains the vanilla code it was checked against, so it stays in `obj/` and never goes into the mod folder. [How a mod gets into the game](/ffx-atel-sharp/concepts/mods/#the-mod-package) describes the package.

## If the build fails

| The build says | What to do |
|---|---|
| `ATLSDK004` | `FFX_GAME_DIR` isn't set in this terminal. The build needs it to name the game's file in the package. |
| `ATLSDK001` | The project has no vanilla source. Check `AtelVanillaSource` in the `.csproj`. |
| `ATLSDK003` | No vanilla source for the namespace. Decompile that script into the folder, or fix the namespace. |
| An `ATL` error in `M009.cs` | The static constructor holds something other than constant assignments. |

## Details

- [What the build does](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/sdk.md#3-build) (docs/sdk.md)
- [A mod on disk](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/formats/mod-package.md#a-mod-on-disk) (docs/formats/mod-package.md)
