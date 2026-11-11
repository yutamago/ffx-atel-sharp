---
title: Licence
description: Atel# is MIT, mods from its template are MIT by default, and the game's names come with the Fahrenheit project's permission.
sidebar:
  order: 4
sources: ["LICENSE", "docs/release.md#decisions-user-2026-11-10", "docs/sdk.md#dotnet-new-atel-mod", "README.md#license"]
---

## Atel#

Atel# is released under the [MIT licence](https://github.com/yutamago/ffx-atel-sharp/blob/main/LICENSE). You may use, change and share it, also in your own tools, as long as you keep the licence notice.

Its bytecode layer is a port of parts of [ffx-rust](https://github.com/yutamago/ffx-rust), which is MIT or Apache 2.0.

## Your mods

A mod made with `dotnet new atel-mod` is MIT by default. The template writes a `LICENSE` file with the current year, an MIT licence entry in the project file, and an `SPDX-License-Identifier: MIT` line at the top of the source file. Pass `--authors "Your Name"` to name yourself as the copyright holder.

If you want another licence, create the mod with `--license none`. The template then writes none of the three, and you add your own.

A mod package holds only your own code and data, never the game's, so you can share it under the licence you choose.

## The game's names

The game library names many of the game's values: monsters, battle commands, actor properties, save fields. Many of these names come from the ID tables and save layout of the [Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) project, and are used with the Fahrenheit project's permission. That permission covers the published packages.

## The game

Final Fantasy X belongs to Square Enix. Atel# contains no game files, and a mod built with it contains none either: the loader links each mod onto the player's own copy of the game.
