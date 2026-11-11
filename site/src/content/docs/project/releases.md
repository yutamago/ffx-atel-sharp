---
title: Releases and versions
description: The packages Atel# ships, where to get them, and how its versions work.
sidebar:
  order: 2
sources: ["docs/release.md", "docs/sdk.md#versions", "docs/editor.md", "docs/website.md#the-cli-as-a-net-tool"]
---

Atel# is still in its first preview. Everything you need to write a mod comes as NuGet packages from nuget.org, and the editor extensions come with each release on GitHub.

## The packages

| Package | What it is | How you get it |
|---|---|---|
| [AtelSharp.Sdk](https://www.nuget.org/packages/AtelSharp.Sdk) | The project SDK a mod builds with: the game library, the analyzers, and the `atel` tool. | Named in your mod's `global.json`. `dotnet build` downloads it. |
| [AtelSharp.Templates](https://www.nuget.org/packages/AtelSharp.Templates) | The `dotnet new atel-mod` template. | `dotnet new install AtelSharp.Templates` |
| AtelSharp.Cli | The `atel` command as a .NET tool. Coming with the next release. | `dotnet tool install -g AtelSharp.Cli` |

All packages of a release have the same version.

The loader (`yuta.atel`) is not one of these packages. It is a Fahrenheit mod; [Get started](/ffx-atel-sharp/get-started/) says where it comes from.

## The editor extensions

The VS Code extension and the Rider plugin show the text of each message and the game's names next to the decompiled source. They are not on the editors' marketplaces yet. Download them from the assets of a [GitHub release](https://github.com/yutamago/ffx-atel-sharp/releases): the `.vsix` for VS Code, the `.zip` for Rider. They have versions of their own.

## Versions

Versions follow [Semantic Versioning](https://semver.org/). A version with a suffix, such as `0.1.0-alpha.1`, is a preview: anything may still change between two of them.

A mod pins the SDK's version in its `global.json`, under `msbuild-sdks`, as `"AtelSharp.Sdk": "0.1.0-alpha.1"`. The template writes this file for you, with its own version. To move a mod to a newer release, change the version there and build again.

Each release is a tag `v<version>` in the repository, with a [GitHub release](https://github.com/yutamago/ffx-atel-sharp/releases) that holds the packages, the editor extensions and the release notes. The [changelog](/ffx-atel-sharp/project/changelog/) lists what changed.

## Details

- [How a release is made](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/release.md) (docs/release.md)
- [The SDK's versions](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/sdk.md#versions) (docs/sdk.md)
- [The editor extensions](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/editor.md) (docs/editor.md)
