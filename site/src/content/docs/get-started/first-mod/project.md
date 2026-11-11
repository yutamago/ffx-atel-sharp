---
title: "2. Create the project"
description: Create the mod project with dotnet new atel-mod, and what each of its files is for.
sidebar:
  order: 2
sources:
  - "docs/sdk.md#2-create-the-project"
  - "docs/sdk.md#dotnet-new-atel-mod"
  - "docs/sdk.md#properties"
  - "docs/formats/mod-package.md#a-mod-on-disk"
---

A mod is a C# project of its own, next to the vanilla source and never inside it. Create it in your mods folder:

```sh
dotnet new atel-mod -n ToughDingo --script m009 --authors your-name
cd ToughDingo
```

- `-n ToughDingo` names the project and its folder.
- `--script m009` is the script the mod changes. The template gives the sample file that script's namespace and class.
- `--authors` is you. Use the name or handle you publish under.

## The files

```
ToughDingo/
  ToughDingo.csproj
  M009.cs
  global.json
  nuget.config
  mod.toml
  README.md
  LICENSE
  .gitignore
```

`ToughDingo.csproj` is short. Its first line, `<Project Sdk="AtelSharp.Sdk">`, makes it an Atel# mod project. Its properties are:

- `AtelModId`, here `your-name.toughdingo`. Fahrenheit knows a mod by this id. It is the name of the mod's folder and the line you add to the load order. A Fahrenheit id starts with its author, so two people's mods never share one.
- `AtelModName`, `Description`, `Authors` and `Version`, which Fahrenheit shows.
- `AtelVanillaSource`, the folder with the vanilla source, here `../atel-vanilla`. The build reads the Dingo's vanilla source from there.

`global.json` pins the version of the SDK, `AtelSharp.Sdk`. It is the version of the template, and `dotnet build` downloads it. To move the mod to a newer release later, you change the version here.

`M009.cs` is a sample. It adds a new script field to the Dingo's script class and shows, in a comment, what a change to a worker looks like. The next page replaces it.

`mod.toml` says how the mod relates to other mods: which ones it needs, which it must load after. This mod needs nothing, so it stays as it is.

`LICENSE` puts the mod under the MIT licence with your name, and the `.cs` files say so in their first line. `--license none` leaves it out, if you want other terms.

`nuget.config` names a local folder as a package source, for an SDK built from the repository between releases. NuGet skips the folder while it doesn't exist, so you can leave it.

## Open it

Open the `ToughDingo` folder in your editor. Because the project names the vanilla source, the editor knows the Dingo's classes too: it completes `M009` and `Ai`, and shows the game library's documentation on hover.

## Details

- [The project and its properties](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/sdk.md#2-create-the-project) (docs/sdk.md)
- [What the template writes](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/sdk.md#dotnet-new-atel-mod) (docs/sdk.md)
