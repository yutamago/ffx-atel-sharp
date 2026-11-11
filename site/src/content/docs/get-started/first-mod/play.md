---
title: "5. Install and play"
description: Install the mod into Fahrenheit, fight a Dingo, and read what the loader logs.
sidebar:
  order: 5
sources:
  - "docs/sdk.md#4-install"
  - "docs/tutorial.md#6-install"
  - "docs/tutorial.md#7-run-and-check"
  - "docs/formats/mod-package.md#the-report"
  - "examples/Besaid/README.md#toughdingo"
  - "examples/Besaid/README.md#the-places"
---

## Install

1. Copy `bin/Debug/net10.0/mods/your-name.toughdingo/` into Fahrenheit's `mods` folder. The folder's name must stay the mod's id.
2. Add the id to `mods/loadorder`, below the loader. With the Quickstart's mod still there, the file reads:

   ```
   yuta.atel
   your-name.giantwakka
   your-name.toughdingo
   ```

The game folder stays as it was. When the game starts, the loader reads the Dingo's script from your copy of the game, applies your package, and Fahrenheit gives the game the changed script in place of the original.

## Fight a Dingo

Start the game through Fahrenheit and load a save in Besaid. Leave the village by its gate. Dingos are among the random battles on the Village Slope, the path just outside, and on the Waterfall Way further on.

What you should see:

- A Dingo takes about ten times as many hits to defeat as before, and it hits harder.
- The victory screen shows 999 gil for each Dingo, instead of 16.

{/* shot 05 */}

Nobody has Scan this early in the game, so you can't read the Dingo's HP in Besaid. With a later save, Scan or Sensor shows 1250 on a Dingo, for example in a battle from the airship.

{/* shot 04 */}

## What the log says

Fahrenheit writes a log for each start, in the `logs` folder of the Fahrenheit folder. The loader's lines start with `Atel loader:`. For this mod you should see one line per changed value, like:

```
info: your-name.toughdingo: m009: sets MonsterStats.Hp (…)
```

and the same for `Strength` and `Gil`. The log is the first place to look when a mod seems to do nothing.

| The log says | What it means |
|---|---|
| `no Atel mods in the load order` | The mod's id isn't in `mods/loadorder`, or its folder has no `atel/` folder. |
| `… differs from the one the mod was built against (hash mismatch)` | Your `m009` isn't the file you built against, for example after a game update. The loader leaves the package out, and the game runs the vanilla Dingo. Decompile and build again. |
| `no scripts built: …` | The loader failed as a whole. The game runs its own scripts. |

## Known limitations

This mod is new and hasn't been run in the game yet. One thing isn't known: the tutorial battles on the island, the ones with Lulu and with Wakka, may use the game's other copies of these monsters. A Dingo there may keep its vanilla stats. If a random Dingo doesn't behave as described, please [open an issue](https://github.com/yutamago/ffx-atel-sharp/issues).

## Next steps

- The [guides](/ffx-atel-sharp/guides/) add code that runs: a new line of dialogue, a monster that heals itself, a counter kept in the save.
- [Concepts](/ffx-atel-sharp/concepts/) explains the script machine behind them.
- To turn the mod off, remove its line from `mods/loadorder`.

## Details

- [Installing](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/tutorial.md#6-install) and [checking in game](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/tutorial.md#7-run-and-check) (docs/tutorial.md)
- [The loader's report](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/formats/mod-package.md#the-report) (docs/formats/mod-package.md)
- [The example's in-game check](https://github.com/yutamago/ffx-atel-sharp/blob/main/examples/Besaid/README.md#toughdingo) (examples/Besaid/README.md)
