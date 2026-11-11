---
title: Work next to a Fahrenheit code mod
description: When a mod needs both script changes and .NET code in the game process, keep them in two projects.
sidebar:
  order: 11
sources:
  - "docs/sdk.md#with-a-fahrenheit-code-mod"
  - "docs/sdk.md#properties"
  - "docs/formats/mod-package.md#a-mod-on-disk"
---

An Atel# mod changes the game's scripts. Some things aren't in a script: a menu of your own, a hook on a function of the game's executable, reading a file. Those need a Fahrenheit code mod, a .NET DLL that Fahrenheit loads into the game. This guide is for a mod that needs both.

## Keep them in two projects

An Atel# mod project must not also be a Fahrenheit DLL project. Its compilation holds the vanilla source, so a DLL built from it would contain the game's code, and you would ship that with the mod. The two SDKs also want different things: Fahrenheit's builds for 32-bit Windows and writes its own manifest.

So a mod with both parts has two projects: the Fahrenheit project with your .NET code, built with Fahrenheit's SDK, and the Atel# project with your script changes, built with `AtelSharp.Sdk`. They can sit side by side in one solution.

## Two mods, or one

### Two mods

The simplest way: each project is a mod of its own, with its own folder and its own manifest. Give the Atel# part an id that shows they belong together, such as `your-name.mymod.atel` next to `your-name.mymod`.

Players install both folders and list both in `mods/loadorder`. If one needs the other, say so in the manifest's dependencies. In the Atel# project, that is `AtelModDependencies`.

### One mod

The Atel# project can write its packages into the Fahrenheit project's mod folder instead, without a manifest of its own. In the Atel# project:

```xml
<AtelModOutputDir>../MyMod/bin/$(Configuration)/net10.0/win-x86/publish/</AtelModOutputDir>
<AtelWriteManifest>false</AtelWriteManifest>
```

Build the Fahrenheit project first, then the Atel# project. Players then get one folder, with your DLL, Fahrenheit's manifest and an `atel/` folder of packages.

The loader takes every mod in the load order that has an `atel/` folder as an Atel# mod, with or without a DLL. One thing changes: Fahrenheit's manifest is the Fahrenheit project's, and it doesn't list `yuta.atel` as a dependency. Fahrenheit then doesn't check that the loader is installed. Tell your players to list `yuta.atel` in `mods/loadorder` themselves.

## Which part does what

- Script changes, new lines of dialogue, monster data, [saved fields](/ffx-atel-sharp/concepts/variables/#saved-fields): the Atel# project.
- Anything in .NET at run time, a settings screen, a hook on the game's executable: the Fahrenheit project.

The two parts can't call each other directly. An Atel# mod's code runs on the game's script machine, with no .NET at run time. [What Atel# can't do](/ffx-atel-sharp/concepts/limits/) explains why.

## Details

- [With a Fahrenheit code mod](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/sdk.md#with-a-fahrenheit-code-mod) (docs/sdk.md)
- [The SDK's properties](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/sdk.md#properties) (docs/sdk.md)
- [Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit), for code mods
