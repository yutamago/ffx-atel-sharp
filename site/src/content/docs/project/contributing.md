---
title: Contributing
description: How to build Atel#, run its tests, find the reference in the repository, and send a change.
sidebar:
  order: 3
sources: ["README.md", "docs/plan.md", "docs/website.md#the-site-is-written-for-people", "docs/source-repo.md"]
---

Atel# takes pull requests on [GitHub](https://github.com/yutamago/ffx-atel-sharp). This page says how to build it, where the reference is, and what a change needs before it can be merged.

## Build and test

You need the .NET 10 SDK. From the repository's root:

```sh
dotnet build ffx-atel-sharp.slnx
dotnet test --no-build ffx-atel-sharp.slnx
```

Most tests run without the game. The corpus tests, which decompile and compile every script the game ships, need your own copy of the game: set `FFX_GAME_DIR` to the folder that holds `FFX.exe`. Without it they skip. Run them when your change can alter what the decompiler or the compiler writes; they take a while and a lot of memory.

The website is an [Astro Starlight](https://starlight.astro.build/) project in `site/`:

```sh
cd site
npm ci
npm run dev
```

`npm run build` checks every link between the site's pages and fails on a broken one.

## Where the reference lives

The `docs/` folder in the repository is the working reference: the encoding rules, the file formats, the measurements of the shipped scripts, and the research behind each decision. It is exact and dense, and it is the source of truth for facts. Start with [docs/plan.md](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/plan.md), which explains the decisions phase by phase and links to the rest.

The pages on this site are written new from `docs/`, for people who are new to it. Each page lists the `docs/` sections it is based on in its front matter (`sources:`). When you change a fact in `docs/`, change the site page that says it in the same pull request.

## Research about the game

What Atel# knows about the game itself, such as file formats, natives, and the names of functions in the game's code, comes from the [ffx-rust](https://github.com/yutamago/ffx-rust) project. New findings about the game go there, as a pull request to that repository, and Atel# then uses them.

## Sending a change

- Open a pull request against `main`. Keep it to one subject.
- CI must be green: it builds and tests on Windows, Linux and macOS, packs the SDK, builds the editor extensions, and builds this site.
- Never commit game files, files extracted from the game, or decompiled scripts. The repository, the packages and the site contain none.
- New code is MIT, like the rest of the repository, with an `SPDX-License-Identifier: MIT` header in each file.

## Details

- [The plan and its decisions](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/plan.md) (docs/plan.md)
- [Building and the projects](https://github.com/yutamago/ffx-atel-sharp/blob/main/README.md#building) (README.md)
- [The plan for this website](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/website.md) (docs/website.md)
