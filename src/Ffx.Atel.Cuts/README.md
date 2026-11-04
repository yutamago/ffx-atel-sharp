# Atel cuts (`yuta.atel.cuts`)

A [Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) mod for the Steam HD Remaster of
Final Fantasy X that switches the named dead blocks of the game's scripts (`Cut.*`,
[`docs/research/dead-code.md`](../../docs/research/dead-code.md)) on and off while the game runs.
Each name is code the original compiler kept behind a `jmp` that the game never takes; switched
on, the code runs where it stands.

It works on its own or with the Atel loader (`yuta.atel`), in any order.

## Install

Build it (.NET 10):

```bash
dotnet build -c Release src/Ffx.Atel.Cuts
```

Copy `artifacts/deploy/release/mods/yuta.atel.cuts` into your Fahrenheit folder's `mods/`, and
add a line `yuta.atel.cuts` to `mods/loadorder`.

## Use

- **F8** shows and hides the menu.
- One checkbox per name, with what the block does. Under each: whether its script is loaded now
  (`Loaded: znkd1300 (ctrl work 0), 29 of 29 block(s) on`) or not (`Not loaded (znkd1300)`).
- A name you have not touched is left as loaded: off in the vanilla file, or on where a linked
  Atel mod switched it on (`[assembly: EnableCut(...)]`). Ticking or unticking the box sets it;
  **as loaded** puts it back.
- A switch applies at once to a loaded script, and again every time the game loads that script
  (a map entered again, the next battle). It only changes what the script does when the
  script reaches the block: the Zanarkand crowd reacts the next time it looks, a battle line
  plays the next time the battle picks one.
- The switches are saved whenever one changes, in Fahrenheit's state folder:
  `state/global/yuta.atel.cuts/Ffx.Atel.Cuts.CutsModule`, JSON:
  `{ "cuts": { "HighroadBattleLine335Tidus": true } }`. Delete the file to start over.
- What the mod did is in its log (`logs/<start time>/Ffx.Atel.Cuts.CutsModule.log` in the Fahrenheit folder):
  each listed script it saw loaded, the blocks it found, each switch it wrote, and anything it
  left alone.

## Before you switch one on

**Some blocks have lasting effects.** They run code the game never ran, and what that code
writes stays in your save: `HighroadBattleLine335Tidus` and `HighroadBattleLine336AuronTidus`
mark their line seen in `BattleDialogLinesSeen` (a bit of the save), so after a save the game
treats the line as played. Others may set story flags or give items. Save to another slot
before trying one, and keep a save from before.

The names, what each does and the scripts they are in:

| Name | Script | Blocks | What it does |
|---|---|---:|---|
| `ZanarkandCrowdFacingReaction` | `znkd1300` (Dream Zanarkand) | 29 | the crowd stops and reacts to whoever is in front of them |
| `HighroadBattleLine335Tidus` | `system_01` (every battle) | 1 | Mi'ihen Highroad battle line 335 with Tidus in the party; sets a `BattleDialogLinesSeen` bit |
| `HighroadBattleLine336AuronTidus` | `system_01` (every battle) | 1 | Mi'ihen Highroad battle line 336 with Auron and Tidus; sets a `BattleDialogLinesSeen` bit |

## With the Atel loader

The two mods don't talk to each other. `yuta.atel` decides which file the game reads (vanilla,
linked with Atel mods, or rebuilt from `.asm`); this mod changes the copy in memory after the game
has read it, so it always patches after the loader. If a linked mod switched a block on, the menu
shows it as on as loaded, and unticking switches it off in memory (the file stays as the loader
wrote it). If a linked or rebuilt file changed the code where a block's `jmp` was, the block is not
found there, and the menu and log say so.

Each Fahrenheit mod loads its own copy of `Ffx.Atel.Bytecode`, so the two can come from different
builds.
