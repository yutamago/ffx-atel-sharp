---
title: Concepts
description: What you need to know about Final Fantasy X's script machine before the guides and the reference make sense.
sidebar:
  order: 0
sources: ["docs/language.md#the-machine-in-brief", "docs/formats/mod-package.md"]
---

Atel# looks like ordinary C#, but the code runs inside Final Fantasy X, on the game's own script machine. That machine works differently from .NET. These pages explain how, using the island of Besaid as the example throughout, the same place the guides use.

Read them in this order. Each one builds on the one before.

1. [What ATEL is](/ffx-atel-sharp/concepts/atel/). The game's script machine: scripts, workers and entries, and the 30 frames a second it runs at.
2. [Requests](/ffx-atel-sharp/concepts/requests/). How one worker asks another to run an entry, and how it waits for the answer.
3. [Variables](/ffx-atel-sharp/concepts/variables/). Script fields, saved fields, registers and locals, and which of them survive a wait, a map change or a save.
4. [How a mod gets into the game](/ffx-atel-sharp/concepts/mods/). From the vanilla source and your mod to the package, the loader and the game, and what happens when two mods change the same thing.
5. [What Atel# can't do](/ffx-atel-sharp/concepts/limits/). Why there is no .NET at run time, and what the analyzers refuse.

If you only want to see a mod working first, the [Get started](/ffx-atel-sharp/get-started/) section needs none of this. Come back here when you want to know why the code looks the way it does.
