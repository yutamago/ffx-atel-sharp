---
title: CLI
description: The atel commands, what each one does and when you use it.
sidebar:
  order: 40
sources: ["docs/tutorial.md", "docs/sdk.md", "docs/editor.md", "docs/source-repo.md#commands"]
---

`atel` is the command-line tool of Atel#. It reads the game's scripts straight from your game folder and never writes into it. Tell it where the game is with the `FFX_GAME_DIR` environment variable, the folder with `FFX.exe`, or with `--game <dir>` on any command:

```bash
export FFX_GAME_DIR=<the folder with FFX.exe>
atel list bjyt
```

`atel --help` prints the usage of every command. Each page below shows that usage for one command, with what it is for.

## Making a mod

| Command | When you use it |
|---|---|
| [`atel decompile`](/ffx-atel-sharp/reference/cli/decompile/) | First: it turns the scripts your mod changes into C#, the vanilla source your mod builds on. |
| [`atel compile-mod`](/ffx-atel-sharp/reference/cli/compile-mod/) | To build a mod package. `dotnet build` runs it for you in an SDK project. |
| [`atel link`](/ffx-atel-sharp/reference/cli/link/) | To check what the loader will make of your mods, before you start the game. |

## Text and names in the editor

| Command | When you use it |
|---|---|
| [`atel text export`](/ffx-atel-sharp/reference/cli/text-export/) | Once after decompiling, so the editor shows each message's text next to `Text[n]`. |
| [`atel names export`](/ffx-atel-sharp/reference/cli/names-export/) | Once after decompiling, so the editor shows the game's names of monsters, commands, places and battles. |

## Working with whole scripts

| Command | When you use it |
|---|---|
| [`atel list`](/ffx-atel-sharp/reference/cli/list/) | To find a script's name. |
| [`atel compile`](/ffx-atel-sharp/reference/cli/compile/) | To compile one changed decompiled script back into a whole script file. |

## For contributors

These commands are for work on Atel# itself: the decompiler, the compiler, the research and the source repository. You don't need them to write a mod.

| Command | What it does |
|---|---|
| [`atel disasm`](/ffx-atel-sharp/reference/cli/disasm/) | Prints a script as ATEL assembly. |
| [`atel asm`](/ffx-atel-sharp/reference/cli/asm/) | Assembles an assembly file onto the original script. |
| [`atel handlers`](/ffx-atel-sharp/reference/cli/handlers/) | Prints the battle's handler tables of monster and battle scripts. |
| [`atel survey`](/ffx-atel-sharp/reference/cli/survey/) | Measures every script of the game. |
| [`atel roundtrip`](/ffx-atel-sharp/reference/cli/roundtrip/) | Decompiles and compiles every script and compares the bytes. |
| [`atel compile-all`](/ffx-atel-sharp/reference/cli/compile-all/) | Compiles a whole decompiled tree and compares it with the game. |
| [`atel export`](/ffx-atel-sharp/reference/cli/export/) | Writes every script into the source repository. |
| [`atel dead`](/ffx-atel-sharp/reference/cli/dead/) | Lists the dead `if (false)` blocks of the game's scripts. |
| [`atel repeats`](/ffx-atel-sharp/reference/cli/repeats/) | Finds code the game's scripts repeat with only different values. |

Details: [docs/tutorial.md](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/tutorial.md) and [docs/sdk.md](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/sdk.md) on GitHub.
