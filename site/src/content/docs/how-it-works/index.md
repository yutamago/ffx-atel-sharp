---
title: How it works
description: What happens between the game's bytecode and your mod in the running game, in four short pages.
sidebar:
  order: 0
sources: ["docs/plan.md", "docs/encoding.md", "docs/formats/mod-package.md", "docs/research/loader.md"]
---

You don't need these pages to write a mod. They are for the curious, and for anyone who wants to work on Atel# itself.

Atel# has four parts. Each page below covers one of them, with a diagram and links to the full reference in the repository.

1. [From bytecode to C#](/ffx-atel-sharp/how-it-works/decompiler/). The decompiler reads a script the game ships and writes it as C# you can read: `if` and `while` instead of jumps, names instead of numbers, members like `Motion.Play(…)` instead of bare function calls.
2. [The same bytes back](/ffx-atel-sharp/how-it-works/compiler/). The compiler turns that C# back into bytecode, and for every script the game ships it gives back exactly the bytes the game has. This is why the vanilla source you edit is the game.
3. [Placing a mod](/ffx-atel-sharp/how-it-works/linker/). The linker puts a mod into the player's copy of a script it has never seen, next to other mods, without moving any of the game's own bytes.
4. [Into the running game](/ffx-atel-sharp/how-it-works/loader/). The loader, a Fahrenheit mod, runs the linker when the game starts and hands the results to the game in place of its own files.
