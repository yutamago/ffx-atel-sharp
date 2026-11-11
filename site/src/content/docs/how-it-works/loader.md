---
title: Into the running game
description: How the loader, a Fahrenheit mod, links the enabled mods when the game starts and hands the results to the game.
sidebar:
  order: 4
sources: ["docs/research/loader.md#how-the-loader-serves-files", "docs/research/loader.md#phase-9-a-linked-mod-package-in-game", "docs/formats/mod-package.md#a-mod-on-disk", "docs/formats/mod-package.md#saved-fields", "docs/formats/debug.md#runtime-injection"]
---

The game itself knows nothing about Atel#. It reads its scripts from its archive, as it always has. The loader makes sure that what it reads is the linked version.

The loader is a Fahrenheit mod called `yuta.atel`. Fahrenheit is the framework that loads mods into the Steam version of the game; your Atel# mod is a Fahrenheit mod too, one that holds only packages and no code of its own.

<figure class="diagram">
<svg viewBox="0 0 540 270" role="img" aria-labelledby="ldr-title">
<title id="ldr-title">At game start the loader reads the mods' packages and the player's game archive, links each script, and Fahrenheit serves the linked scripts to the game in place of the originals.</title>
<defs><marker id="ldr-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path class="head" d="M0,0 L10,5 L0,10 z"/></marker></defs>
<rect class="box" x="10" y="20" width="160" height="60" rx="6"/>
<text x="90" y="46" text-anchor="middle" class="label">Your mods</text>
<text x="90" y="66" text-anchor="middle" class="small">packages, in load order</text>
<rect class="box" x="10" y="110" width="160" height="60" rx="6"/>
<text x="90" y="136" text-anchor="middle" class="label">The game's archive</text>
<text x="90" y="156" text-anchor="middle" class="small">the player's own files</text>
<rect class="accent" x="200" y="65" width="140" height="60" rx="6"/>
<text x="270" y="91" text-anchor="middle" class="label">The loader</text>
<text x="270" y="111" text-anchor="middle" class="small">links each script</text>
<rect class="box" x="370" y="20" width="160" height="60" rx="6"/>
<text x="450" y="46" text-anchor="middle" class="label">Linked scripts</text>
<text x="450" y="66" text-anchor="middle" class="small">in the loader's folder</text>
<rect class="box" x="370" y="110" width="160" height="60" rx="6"/>
<text x="450" y="136" text-anchor="middle" class="label">Fahrenheit</text>
<text x="450" y="156" text-anchor="middle" class="small">serves them in place</text>
<rect class="accent" x="370" y="200" width="160" height="50" rx="6"/>
<text x="450" y="231" text-anchor="middle" class="label">The game</text>
<path class="line" d="M172,50 C186,50 186,86 196,86" marker-end="url(#ldr-arrow)"/>
<path class="line" d="M172,140 C186,140 186,104 196,104" marker-end="url(#ldr-arrow)"/>
<path class="line" d="M342,95 C356,95 356,50 366,50" marker-end="url(#ldr-arrow)"/>
<path class="line" d="M450,82 L450,106" marker-end="url(#ldr-arrow)"/>
<path class="line" d="M450,172 L450,196" marker-end="url(#ldr-arrow)"/>
<text x="10" y="231" class="small">once, when the game starts</text>
</svg>
<figcaption>The loader links once, at start. From then on the game reads the linked scripts as if they were its own.</figcaption>
</figure>

## At game start

When Fahrenheit starts the game, it sets up every mod before the game reads its scripts. In that moment the loader:

1. reads Fahrenheit's load order and finds every mod with Atel# packages in it;
2. reads the vanilla file of each script those packages change, from the player's own game archive;
3. runs the linker on each one, with the packages in load order;
4. writes the linked files into its own folder, and a report of what it did into the log.

Fahrenheit's file loader then serves those files to the game in place of the archive's, the same way it serves any mod's replacement file. The game loads a linked script like any other, also when the script has grown.

## When something doesn't fit

A failed link never stops the game. A package that doesn't match the player's file is left out, the game runs the vanilla script, and the log says which mod and why. The other mods still load.

Since the loader links on the player's machine, an update of a mod is just a new folder of packages. There is no patched game file to ship or to keep in step with other mods.

## Saved fields at run time

The loader also keeps the values of every mod's saved fields. When you start a new game or load a save, it puts their values into place. When you save, it writes them to its own file beside the save, by mod and field name. The save file itself stays as the game would write it.

## While you develop

Two things help while you write a mod:

- `Debug.Print(…)` writes a line to the loader's log and to the screen. F6 shows or hides the lines.
- A package dropped into the loader's `inject` folder while the game runs is linked at once. Its code takes effect at once where the running script allows it, and otherwise the next time the game loads that script, for example when you enter the map again. New message text shows at once.

## Details

- [How the loader serves files through Fahrenheit](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/research/loader.md#how-the-loader-serves-files) (docs/research/loader.md)
- [A linked mod, checked in the game](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/research/loader.md#phase-9-a-linked-mod-package-in-game) (docs/research/loader.md)
- [A mod on disk](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/formats/mod-package.md#a-mod-on-disk) (docs/formats/mod-package.md)
- [Saved fields at run time](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/formats/mod-package.md#saved-fields) (docs/formats/mod-package.md)
- [Debug text and runtime injection](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/formats/debug.md#runtime-injection) (docs/formats/debug.md)
