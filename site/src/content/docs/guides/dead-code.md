---
title: Switch dead code on
description: The game ships code it never runs. Named blocks of it can be switched on by a mod, or from a menu while the game runs.
sidebar:
  order: 9
sources:
  - "docs/sdk.md#switching-dead-code-on"
  - "docs/research/dead-code.md#what-the-dead-code-is"
  - "docs/research/dead-code.md#names"
  - "docs/research/dead-code.md#switching-a-block-on"
  - "docs/research/dead-code.md#switching-a-block-on-in-game"
  - "examples/Besaid/README.md#a-dead-block-in-besaid"
---

This guide is different from the others: it has no Besaid mod. Besaid has no dead code worth switching on. The guide explains the mechanism with a block from the game's opening instead, and it is honest about what that block is known to do.

## What dead code is

The game's scripts were compiled from a source language with constant conditions. Where a condition was always false, the original compiler kept the code and jumped over it. The code is still in the files, and the game never runs it.

The decompiler writes such a block as `if (false) { … }`. Most of it is not cut content. The scripts were written with macros, and each copy of a macro kept the branches meant for the other copies. Switching one of those on would run the wrong copy's code.

A few blocks are understood. Those get a name instead of `false`:

```csharp
if (Cut.ZanarkandCrowdFacingReaction)
{
    …
}
```

Each name is a constant of the class `Cut` in the game library, always `false`, so the vanilla source still compiles to the game's bytes. Its documentation comment says what the block does. Hover over it in your editor to read it.

## In Besaid

`atel dead` lists every dead block in the game's scripts. In Besaid it finds two, both empty, in the camera code of the battle with Kimahri. The village, the island and the monsters of the course have none. There is nothing to see in Besaid, so this guide uses a block from elsewhere.

## The example: the crowd in Zanarkand

At the very start of the game, Tidus walks through a crowd in Zanarkand, the script `znkd1300`. Each person in the crowd has a dead block named `Cut.ZanarkandCrowdFacingReaction`. Read from the code, it does this: when Tidus is near, the person looks for another person close by and in front of them, stops walking, plays a reaction motion, waits a moment, and looks again.

That is what the code says. What it looks like is not known. The block was switched on in the game once, with the in-game switch described below. The switch worked, and the game ran normally, but nobody could see a difference in the crowd. It may run only when Tidus stands near the right people, or the reaction may be too brief to notice.

## Switch it on in a mod

A mod switches a named block on with one assembly attribute, `[assembly: EnableCut(nameof(Cut.ZanarkandCrowdFacingReaction))]`, in any `.cs` file of the mod, with `using Ffx;` and `using Ffx.Atel;` above it. A file with only these lines needs no namespace, and the mod needs no vanilla source for it.

`nameof` means the C# compiler checks the name: a typo doesn't build, and the editor completes it.

The build adds the switch to the package of each script that has the block, `znkd1300` here. When the game starts, the loader finds the blocks in your copy of the script by their code, and turns each jump over them into an instruction that does nothing, of the same length. The block then runs where it stands, exactly as if its condition were `true`, and nothing else in the script moves.

If your copy of the script doesn't have the block, the build stops. If the player's copy doesn't, the loader leaves that script's package out and logs an error.

Read the documentation comment before you switch a block on. Some blocks change the save: one of the named battle lines on the Mi'ihen Highroad marks itself as seen.

## Switch it on while the game runs

To try a block without building a mod, there is a separate Fahrenheit mod, `yuta.atel.cuts`. It shows a menu on F8 with a checkbox for each named block, and flips the same jump in the script the game has loaded. A switch applies at once, and again each time the game loads that script.

It isn't released yet either. Build it from the repository as you built the loader:

```sh
dotnet build -c Release src/Ffx.Atel.Cuts
```

Copy `artifacts/deploy/release/mods/yuta.atel.cuts/` into Fahrenheit's `mods` folder and add `yuta.atel.cuts` to `mods/loadorder`. It works with or without the loader.

## What can go wrong

- **The build says the name has no blocks in your script.** Your game files differ from the ones the name was found in.
- **Nothing visible happens.** The block only changes what the script does when the script reaches it. For the crowd, that means Tidus near the crowd, in the opening.

## Next

[Find the script you want to change](/ffx-atel-sharp/guides/find-a-script/) shows how to find your way to any script, worker and entry.

## Details

- [Switching dead code on](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/sdk.md#switching-dead-code-on) (docs/sdk.md)
- [The catalogue of dead code, and the names](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/research/dead-code.md) (docs/research/dead-code.md)
- [The in-game switch](https://github.com/yutamago/ffx-atel-sharp/blob/main/src/Ffx.Atel.Cuts/README.md) (src/Ffx.Atel.Cuts/README.md)
