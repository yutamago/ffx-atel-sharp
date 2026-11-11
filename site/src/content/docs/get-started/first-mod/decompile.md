---
title: "1. Read the Dingo's script"
description: Decompile the Dingo's monster script and find your way around the C# the decompiler writes.
sidebar:
  order: 1
sources:
  - "docs/tutorial.md#3-decompile-the-larva"
  - "docs/sdk.md#1-decompile-the-vanilla-source"
  - "docs/language.md#scripts"
  - "docs/research/handlers.md#actions"
---

Every monster has a script of its own, and every battle with that monster runs it. The Dingo's script is `m009`. The `m` is for monster and the number is the monster's number in the game's tables. [Find the script you want to change](/ffx-atel-sharp/guides/find-a-script/) shows how to find the number of another monster.

## Decompile it

Go to your mods folder, the one from the Quickstart, with `FFX_GAME_DIR` set:

```sh
cd ffx-mods
atel decompile m009 -o atel-vanilla
```

`atel` reads the Dingo's file from your copy of the game and writes it as C# into `atel-vanilla/`. If the folder is already there from the Quickstart, the new script goes next to the one already in it. You can keep the vanilla source of many scripts in one folder.

This C# is the vanilla source: the game's own code, unchanged. It compiles back to exactly the bytes it came from, so you can trust that it says what the game does. It is also the game's code, so it stays on your machine. Don't commit it to a repository or share it.

## What's in the folder

`atel-vanilla/` is a solution, `Atel.slnx`, with a project per script. Open it in your editor. The Dingo is in `Monsters/M009/`:

- `M009.cs` is the script class. It lists the script's workers and holds its script fields.
- One file per worker. A worker is one piece of the script that runs on its own. `Ai.cs` is the worker the battle asks what the Dingo does.
- `M009.Data.cs` and `M009.Layout.cs` hold the script's data, and what keeps the compiled bytes exactly the game's. You won't need to edit them.

[What ATEL is](/ffx-atel-sharp/concepts/atel/#workers) explains scripts, workers and entries in more detail.

## Look at the Ai worker

Open `Ai.cs`. Its methods are its [entries](/ffx-atel-sharp/concepts/atel/#entries), the pieces of code the battle starts:

- `Init` runs when the Dingo enters the battle.
- `OnTurn` runs when its turn comes. It picks a target and an attack.
- `OnHit` runs when a command hurts it, and `OnDeath` when it dies.

Hover over a call in your editor to see what it does. The game library documents each one.

The HP, strength and gil you want to change aren't in this code. They are values in the monster's data, which the battle reads when the Dingo enters. So this mod doesn't touch `Ai` at all. It changes those values, in the script class.

## Details

- [Decompiling](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/sdk.md#1-decompile-the-vanilla-source) (docs/sdk.md)
- [Which worker and entry the battle runs](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/research/handlers.md#actions) (docs/research/handlers.md)
