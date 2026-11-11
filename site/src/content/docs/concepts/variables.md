---
title: Variables
description: Script fields, saved fields, registers and locals, and which of them survive a wait, a map change, a battle or a save.
sidebar:
  order: 3
sources:
  - "docs/language.md#variables"
  - "docs/language.md#registers"
  - "docs/language.md#locals"
  - "docs/language.md#saved-fields"
  - "docs/language.md#mods"
  - "docs/research/mod-saves.md"
---

One of the guides makes the old woman in Besaid village give you 1,000 gil, once per save. The mod has to remember that she already did. Where it keeps that one flag decides how long the memory lasts. Keep it in the wrong place and she forgets the moment you leave the village, or she remembers it in every save you ever load.

ATEL has several places for a value, and each one lives for a different time. This page goes through them from the shortest-lived to the longest.

## Locals

A local is an ordinary C# local variable inside an entry: `int n = 3;`, `bool done = …;`, a `for` loop's counter. It lives while the entry runs, and the compiler makes sure it keeps its value across waits, as you would expect from C#.

Locals are `int`, `float`, `bool`, enums and the smaller integer types. Arrays, `long`, `double` and `string` are not possible, because the machine has nowhere to keep them.

## Registers

Each worker has a few registers: four for integers and ten for floats. Vanilla code uses them a lot, and the decompiler shows them as fields of the worker with `[Register(n)]`, such as `I0` or `F1`.

A register belongs to the whole worker, not to one entry. Every entry of the worker sees the same registers. So when an entry waits, another entry of the same worker may run in the meantime and change them. The old woman's `OnTalk` can run while her `Main` is waiting in the middle of a walk, and both use her registers.

In new code, prefer locals. Use a register field only where you read a value the vanilla code left there. The analyzers warn when a register is used across a wait while another entry of the worker writes it.

## Script fields

A script field is a static field of the script class. All the workers of the script share it: one worker writes it, another reads it.

In vanilla source, the decompiler writes them with an attribute that says where the value lives, such as `[Var(Storage.Shared, …)]`. In a mod, a new script field is a plain static field of your part of the script class, and the linker finds room for it: `public static int Turns = 5;`. The initializer is its starting value.

A script field lives as long as the script is loaded. The next time the game loads the script, when you walk back into the village or the next battle starts, it is back at its starting value. In a battle, two Dingos run the same `m009` script, so they share its script fields too. A turn counter in a script field counts the turns of both.

Each worker also has a little private data of its own, and the decompiler shows those variables as script fields marked `Storage.Private`. Every worker keeps its own copy. In battle the game clears a worker's private data when it sets the worker up.

## Save game fields

The game keeps its own progress in the save game, and scripts read and write it as variables. The best known is `StoryProgress`, the number that says how far the story has got. The decompiler writes these as fields with `[Var(Storage.Save, …)]`.

They survive everything a save survives, because they are the save. Writing to one changes the player's game, so only do it when you mean to, and with a field you know.

## Saved fields

A saved field is a variable of your mod that is kept with the save game. You mark a static field `[Saved]`, and code reads and writes it like any other variable:

- `[Saved] public static byte Given;` in a static class of the mod. Every script of the mod can use it.
- or the same in your part of a script class, for state only that script needs.

This is the right place for the old woman's flag. On a new game, or a save made before the mod was installed, the field has its starting value, so she gives the gil. Once the mod sets it, it stays set through map changes, battles, saving and loading.

The save file the game writes stays vanilla. [The loader](/ffx-atel-sharp/concepts/mods/#the-loader) keeps your mod's values in its own file next to each save, under the name of the field, and puts them back when you load. That has two consequences:

- Removing the mod leaves the save untouched. Enabling it again brings its values back.
- Renaming the field or its class makes it a different field, and the old value is no longer found. The `Key` setting of `[Saved]` keeps an old name.

A mod can also read another mod's saved field, if it declares that mod as required. It can only read it. The field is the other mod's to write.

## What survives what

| | A wait | The end of the entry | The script is loaded again | Saving and loading |
|---|---|---|---|---|
| Local | yes | no | no | no |
| Register | not safely | yes, shared by every entry of the worker | no | no |
| Script field | yes | yes | no, back to its starting value | no |
| Save game field | yes | yes | yes | yes, it is the save |
| Saved field | yes | yes | yes | yes, kept by the loader |

"The script is loaded again" covers walking into a map again and every new battle with that monster.

Scripts can also reach common variables that all scripts share. The game uses few of them and most have no names yet, so the guides don't use them.

## Details

The storage areas, the variable types and the rules for saved fields are in [docs/language.md, "Variables"](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/language.md#variables) and ["Saved fields"](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/language.md#saved-fields). Why saved fields are kept by name and not in the save file is in [docs/research/mod-saves.md](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/research/mod-saves.md).
