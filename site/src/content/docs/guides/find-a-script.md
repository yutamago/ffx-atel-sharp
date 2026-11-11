---
title: Find the script you want to change
description: From something you see in the game to the script, the worker and the entry a mod has to change.
sidebar:
  order: 10
sources:
  - "docs/language.md#scripts"
  - "docs/language.md#workers"
  - "docs/research/handlers.md"
  - "docs/corpus.md#story-progress"
  - "docs/editor.md#names-atel-names-export"
  - "examples/Besaid/README.md#the-places"
---

Every mod starts with the same question: where does the game do the thing I want to change? A mod names three things. The script, which is the namespace. The worker, which is the class. And the entry, the method its patch attaches to. This guide finds each one, with Besaid as the example.

## The script

### A place

Each map has an event script, named after its area and a number. `bs` is Besaid, `bsvr` the village and `bsil` the island around it. `atel list` lists the game's scripts, and takes part of a name to filter them:

```sh
atel list bsvr
```

The editors show the game's own place names. With the names exported (`atel names export -o atel-vanilla`, see [Set up VS Code or Rider](/ffx-atel-sharp/guides/editors/)), each `Map.Bsvr0000` in the source shows the name of its save location next to it.

### A place at a moment in the story

One place can have several scripts. Besaid village is `bsvr0000` on most visits, `bsvr0002` when Tidus first arrives with Wakka, `bsvr0001` on the night after the trial, and another script for the celebration. The game loads the one that fits the story.

So when a mod does nothing, first check that it changes the script the game loads at that moment. [Print debug text in the game](/ffx-atel-sharp/guides/debug-text/) prints the script's name and the story counter while you walk around, which tells you.

Inside one script, code often depends on the story too. `if (StoryProgress < Story.S00208_Bsvr0000)` and similar tests decide what a map does at each visit. Each `Story` constant is named after its value and the script that stores it, and a higher value is later in the story.

### A monster

Each monster has a script named `m` and a number: the Dingo is `m009`. The number is the monster's number in the game's monster table.

To find a monster's number by its name, export the names and search the monster table:

```sh
atel names export -o atel-vanilla
```

Open `atel-vanilla/.atel/names/en/monsters.json` and search for the name. Its entry has an `id`, and the script's number is that `id` minus 4096. The Dingo's id is 4105, so its script is `m009`. The entry's `member` is the monster's name in the source, such as `Target.Wolf01` for the Dingo.

### A battle

A battle, one formation of monsters in a place, has a script of its own, named after the place: `bsil07_00` is a battle on the Village Slope. It runs the battle's own cameras and events. To change what a monster does, change the monster's script. To change one battle, change the battle's.

## The worker

Decompile the script and open its folder. The script class, named after the script, lists the workers in order. Each worker has a file.

The decompiler names workers as well as it can:

- An actor is named after the model it loads: `Wakka`, `Tidus`, or `SprsHan039_33` for Besaid's old woman, after her model.
- A monster's workers are named after the job the battle gives them: `Ai` decides the monster's moves.
- A worker it can't name keeps its number: `W19`.

To find a villager, the quickest way is often their words. Export the script's text, then search it for a line you remember:

```sh
atel text export bsvr0000 -o atel-vanilla
```

`atel-vanilla/.atel/text/en/bsvr0000.json` lists the script's messages in order, and a message's position is its number. Search the source for `Text[` with that number, and the worker that uses it is the one who says it. With the editor extension installed, every `Text[n]` shows its text next to it, so you can read the conversation in the source.

## The entry

An entry is the method a patch attaches to. Which one depends on what starts the code:

| You want to change | The entry |
|---|---|
| what happens when the map loads | `Init` of the worker, or of the player's worker |
| what an actor does all the time, such as a walk | `Main` |
| what happens when you talk to someone | their `OnTalk` |
| what a monster does on its turn | `Ai.OnTurn` |
| what happens when a monster is hit or dies | `Ai.OnHit`, `Ai.OnDeath` |

Monster and battle scripts have more: when a monster enters, when its turn starts or ends, the cameras. `atel handlers m009` prints which worker and which entry of a script the battle runs for each of them.

Then read the entry. A postfix runs after it, a prefix before it, and `[Inject]` at a statement inside it, so the code tells you where yours should go.

## Look where the game already does it

The quickest way to learn how to change something is to find a place where the game already does it. The beach's script sets Tidus's run speed, and that's where the [Move faster](/ffx-atel-sharp/guides/move-faster/) guide found the call. The celebration's script plays the motion the [dancing villager](/ffx-atel-sharp/guides/run-code-on-talk/) uses. Decompile a script where it happens, and search it.

`atel decompile all -o <folder>` decompiles every script, for searching across the whole game. It takes a while, so run it once and keep the folder.

## Details

- [Scripts and workers](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/language.md#scripts) (docs/language.md)
- [Which worker and entry the battle runs for which event](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/research/handlers.md) (docs/research/handlers.md)
- [The game's names in the editor](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/editor.md#names-atel-names-export) (docs/editor.md)
- [Story progress](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/corpus.md#story-progress) (docs/corpus.md)
