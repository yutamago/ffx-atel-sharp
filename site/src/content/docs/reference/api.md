---
title: Game library API
description: The classes, members and attributes your mod's C# uses, with the ones you need most first.
sidebar:
  order: 30
sources: ["docs/language.md#natives", "docs/language.md#members", "docs/language.md#mods", "docs/research/oo-surface.md"]
---

The game library is the C# your mod compiles against: the natives the game offers, the members of a worker, the attributes that attach your code to the game's, and the enums that name its maps, battles and monsters. It is the assembly `Ffx.Atel.Game`, which the Atel# SDK references for you.

None of it runs on .NET. When you write `Wait.Seconds(1.5f)` or `Self.Hp = 100;`, the compiler turns the call into the instructions the game's own scripts use. The pages here show each declaration and what it does in the game.

## Start here

These are the types a mod uses most.

| Type | What it is for |
|---|---|
| [`Worker`](/ffx-atel-sharp/reference/api/ffx-atel/worker/) | The base of every worker. Its members are what your code can use without a class name: `Pos`, `Yaw`, `Model`, `Text`, and the components below. |
| [`Turn`](/ffx-atel-sharp/reference/api/ffx/turncontrol/), [`Move`](/ffx-atel-sharp/reference/api/ffx/movecontrol/), [`Motion`](/ffx-atel-sharp/reference/api/ffx/motioncontrol/), [`Neck`](/ffx-atel-sharp/reference/api/ffx/neckcontrol/) | A worker's components: turning, walking to a point, playing an animation, looking at something. |
| [`Dialogue`](/ffx-atel-sharp/reference/api/ffx/dialogue/) | A line of dialogue in a text box as one call: `Dialogue.Say(Text[71]);`. |
| [`Text`](/ffx-atel-sharp/reference/api/ffx/texttable/) | The script's messages: `Text[12]`, and `Text.New("…")` for a line of your own. |
| [`Wait`](/ffx-atel-sharp/reference/api/ffx/wait/) | Pauses the code for some frames, some seconds or for ever. |
| [`Camera`](/ffx-atel-sharp/reference/api/ffx/camera/) | Where the camera is ([`Camera.Eye`](/ffx-atel-sharp/reference/api/ffx/camera-eye/)), what it looks at ([`Camera.Focus`](/ffx-atel-sharp/reference/api/ffx/camera-focus/)), its moves and its shakes. |
| [`Btl`](/ffx-atel-sharp/reference/api/ffx/btl/) | Battle natives for monster and battle scripts: actors, commands, targets, damage and the end of a battle. |
| [`Actors`](/ffx-atel-sharp/reference/api/ffx/pickedactors/), [`Self`](/ffx-atel-sharp/reference/api/ffx/ownactor/) | A battle actor's stats as properties: `Actors[Target.AllPly].Hp`, `Self.Hp`. |
| [`MonsterStats`](/ffx-atel-sharp/reference/api/ffx/monsterstats/) | A monster's HP, MP, strength and the rest, set in its script class's static constructor. |
| [`MonsterRewards`](/ffx-atel-sharp/reference/api/ffx/monsterrewards/) | The gil, AP, drops and steals a monster gives. |
| [`Save`](/ffx-atel-sharp/reference/api/ffx/save/) | The save game's fields a script can read and write, such as the story progress. |
| [`Debug`](/ffx-atel-sharp/reference/api/ffx/debug/) | Prints values to Fahrenheit's log while you test a mod. |
| [`Common`](/ffx-atel-sharp/reference/api/ffx/common/) | The game's general natives: moving and turning, the controller, triggers, text boxes, map changes. |
| [`Chr`](/ffx-atel-sharp/reference/api/ffx/chr/) | Natives for a worker's character model: motions, size, shadow, collision. |
| [`Screen`](/ffx-atel-sharp/reference/api/ffx/screen/) | Fades, colour filters, lights and the map's drawing. |

## Attributes for mods

A mod adds to the game's scripts with `partial` classes. These attributes say where your code goes.

| Attribute | What it does |
|---|---|
| [`[Postfix]`](/ffx-atel-sharp/reference/api/ffx-atel/postfixattribute/) | Runs your method after a vanilla entry: `[Postfix(nameof(OnTalk))]`. |
| [`[Prefix]`](/ffx-atel-sharp/reference/api/ffx-atel/prefixattribute/) | Runs your method before a vanilla entry. |
| [`[Replace]`](/ffx-atel-sharp/reference/api/ffx-atel/replaceattribute/) | Runs your method instead of a vanilla entry. |
| [`[Inject]`](/ffx-atel-sharp/reference/api/ffx-atel/injectattribute/) | Runs your method inside a vanilla entry, before or after a given statement. |
| [`[Saved]`](/ffx-atel-sharp/reference/api/ffx-atel/savedattribute/) | Keeps a field's value in the player's save game. |

## Names for game values

Enums and constant classes give the game's numbers a name, so the code says `Map.Bjyt0400` instead of a number: [`Map`](/ffx-atel-sharp/reference/api/ffx/map/), [`Battle`](/ffx-atel-sharp/reference/api/ffx/battle/), [`Target`](/ffx-atel-sharp/reference/api/ffx/target/), [`Command`](/ffx-atel-sharp/reference/api/ffx/command/), [`Model`](/ffx-atel-sharp/reference/api/ffx/model/), [`Sfx`](/ffx-atel-sharp/reference/api/ffx/sfx/), [`ActorProperty`](/ffx-atel-sharp/reference/api/ffx/actorproperty/), [`Story`](/ffx-atel-sharp/reference/api/ffx/story/) and [`Cut`](/ffx-atel-sharp/reference/api/ffx/cut/).

## Every type

- [Namespace `Ffx`](/ffx-atel-sharp/reference/api/ffx/): the game's API, all the native classes, the worker components, the enums, the save fields.
- [Namespace `Ffx.Atel`](/ffx-atel-sharp/reference/api/ffx-atel/): the worker base classes, every attribute, and the task types behind requests.

## Reading these pages

Each member shows its declaration first, then what it does. Many of the game's natives are not described yet: their page says "Not described yet". What research has found about them is in the collapsed "Details" block, with the native's number and how often the game's scripts call it.

A worker member is often the plainer form of a native: `Turn.Start()` is the native `Common.TurnStart`. Use the member where there is one; the decompiler writes members where it can, too.

The text of these pages comes from the XML comments in the library's source, so your IDE shows the same text when you hover a call.

Details: [docs/language.md, "Natives" and "Members"](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/language.md#natives) on GitHub.
