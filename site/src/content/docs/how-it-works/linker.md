---
title: Placing a mod
description: How the linker puts a mod into the player's copy of a script it has never seen, next to other mods.
sidebar:
  order: 3
sources: ["docs/formats/mod-package.md#a-package", "docs/formats/mod-package.md#linking", "docs/formats/mod-package.md#data-edits", "docs/formats/mod-package.md#saved-fields", "docs/language.md#mods", "docs/research/mod-saves.md"]
---

You build your mod against the vanilla source on your own machine. But the mod you share can't contain the game's code, and it has to work next to mods you have never heard of. The linker solves both: it runs on the player's machine and places each mod into the player's own copy of the script.

## What a mod package holds

When you build, Atel# compiles only your own code: the new methods, the prefixes and postfixes, the new variables, the values you change. Anything of the game's that your code refers to, such as a vanilla method or a variable, is written as a reference by number, not copied.

Next to each reference, the package keeps a hash of the vanilla part it relies on. The hash doesn't contain the game's code, but it lets the linker check that the player's script is the one you built against.

## The linker's steps

The linker takes the player's file and every mod's package for that script, in Fahrenheit's load order.

1. **Check.** A package whose hashes don't match the player's file is left out for this script, with an error in the report. The other mods still apply.
2. **Number.** New variables, workers and entries get the next free numbers, mod after mod. All numbers are known before any code is placed, so your code can request an entry your own mod adds.
3. **Place.** All patches on one vanilla method, from every mod, become one chain: the prefixes in load order, then the method, then the postfixes. The method's entry now starts at the chain.
4. **Lay out.** The new code and the tables that grew go after the end of the script. The game's own bytes stay where they were.

<figure class="diagram">
<svg viewBox="0 0 540 270" role="img" aria-labelledby="lnk-title">
<title id="lnk-title">A linked script: the game's block unchanged, the mod's code added after its end, and the patched method's entry pointing at a chain of prefix, method and postfix.</title>
<defs><marker id="lnk-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path class="head" d="M0,0 L10,5 L0,10 z"/></marker></defs>
<text x="185" y="24" text-anchor="middle" class="small">the game's bytes, each at its place</text>
<text x="452" y="24" text-anchor="middle" class="small">added</text>
<path class="line" d="M10,34 L10,30 L360,30 L360,34"/>
<path class="line" d="M372,34 L372,30 L530,30 L530,34"/>
<rect class="box" x="10" y="44" width="60" height="50"/>
<text x="40" y="74" text-anchor="middle" class="small">header</text>
<rect class="box" x="70" y="44" width="100" height="50"/>
<text x="120" y="74" text-anchor="middle">worker 0</text>
<rect class="box" x="170" y="44" width="120" height="50"/>
<text x="230" y="74" text-anchor="middle">worker 1</text>
<rect class="box" x="290" y="44" width="70" height="50"/>
<text x="325" y="74" text-anchor="middle" class="small">data</text>
<rect class="accent" x="372" y="44" width="58" height="50"/>
<text x="401" y="74" text-anchor="middle" class="small">tables</text>
<rect class="accent" x="430" y="44" width="100" height="50"/>
<text x="480" y="66" text-anchor="middle">worker 1</text>
<text x="480" y="84" text-anchor="middle" class="small">new code</text>
<text x="230" y="116" text-anchor="middle" class="small">vanilla method stays</text>
<path class="line dashed" d="M430,96 L190,176"/>
<path class="line dashed" d="M530,96 L530,176"/>
<rect class="accent" x="190" y="176" width="105" height="44" rx="6"/>
<text x="242" y="203" text-anchor="middle">prefixes</text>
<rect class="accent" x="307" y="176" width="105" height="44" rx="6"/>
<text x="359" y="203" text-anchor="middle">the method</text>
<rect class="accent" x="424" y="176" width="106" height="44" rx="6"/>
<text x="477" y="203" text-anchor="middle">postfixes</text>
<path class="line" d="M295,198 L305,198" marker-end="url(#lnk-arrow)"/>
<path class="line" d="M412,198 L422,198" marker-end="url(#lnk-arrow)"/>
<rect class="box" x="10" y="176" width="120" height="44" rx="6"/>
<text x="70" y="203" text-anchor="middle">the entry</text>
<path class="line" d="M132,198 L186,198" marker-end="url(#lnk-arrow)"/>
<text x="360" y="250" text-anchor="middle" class="small">the chain, in load order</text>
</svg>
<figcaption>A linked script. The game's block keeps every byte at its place, and the chain for the patched method goes after its end.</figcaption>
</figure>

## Why nothing moves

A script is more than code. Its data holds positions inside the script itself, and the game follows those positions. If the linker moved the game's bytes to make room, those positions would point at the wrong place.

So the linker never makes room. The vanilla code stays at its place, and the chain runs a copy of the patched method (or jumps to it, when there are only prefixes). The rest of the script runs exactly as before. When the patched method is the first one in its worker, its first instructions become a jump to the new code.

## Changing values

A mod can also change single values, such as a monster's HP, in a static constructor. Each assignment becomes a small edit: these bytes, at this place, in this part of the data. The linker writes it into the player's file in place.

When two mods write the same byte, the later one in the load order wins, and the report says so. Edits of different bytes combine.

## Saved fields

A field marked `[Saved]` keeps its value per save, like the game's own progress. While the game runs, the game's save data has an unused area at its end, and the linker gives each mod's saved fields a place in it, in load order, every time the game starts.

The values are kept beside the save, not in it. The loader writes them to a file of its own, under the mod's name and the field's name, not by their place. So the save stays a vanilla save, a saved field keeps its value when you add or remove another mod, and another mod can read it by name.

## Details

- [What a package holds](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/formats/mod-package.md#a-package) (docs/formats/mod-package.md)
- [Linking, step by step](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/formats/mod-package.md#linking) (docs/formats/mod-package.md)
- [Data edits](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/formats/mod-package.md#data-edits) and [saved fields](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/formats/mod-package.md#saved-fields) (docs/formats/mod-package.md)
- [Where saved fields live in the save](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/research/mod-saves.md) (docs/research/mod-saves.md)
- [What a mod can declare](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/language.md#mods) (docs/language.md)
- [The design of mods](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/plan.md#phase-9-mods) (docs/plan.md)
