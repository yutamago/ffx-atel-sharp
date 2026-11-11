---
title: Set up VS Code or Rider
description: Show each message's text and the game's names next to the decompiled source, in VS Code or Rider.
sidebar:
  order: 12
sources:
  - "docs/editor.md"
  - "docs/editor.md#atel-text-export"
  - "docs/editor.md#names-atel-names-export"
  - "docs/editor.md#how-a-message-shows"
  - "docs/editor.md#vs-code"
  - "docs/editor.md#installing"
---

Any C# editor works for Atel# mods: completion, the game library's documentation on hover, and the analyzers' errors as you type. What a plain C# editor can't show is the game's text. The source names a line of dialogue by its number, `Text[12]`, and a monster by its internal name, `Target.Wolf01`. The extensions for VS Code and Rider show the text and the name you know from the game next to them.

{/* shot 17 */}

## Export the text and names

The extensions read the text from your own game, through files `atel` writes next to the vanilla source. In your mods folder:

```sh
atel text export -o atel-vanilla
atel names export -o atel-vanilla
```

The first writes every script's messages, the second the names of monsters, commands, maps and battles. Both go into `atel-vanilla/.atel/`, in every language the game has. `--lang en` exports one language only, and `atel text export bsvr0000 -o atel-vanilla` the messages of one script.

These files are game text. Like the vanilla source, they stay on your machine.

## VS Code

1. Install Microsoft's C# extension, if you haven't.
2. Download the `.vsix` from the latest [GitHub release](https://github.com/yutamago/ffx-atel-sharp/releases) and install it: `code --install-extension <the file>.vsix`, or **Install from VSIX…** in the Extensions view's menu.
3. Open the `atel-vanilla` folder, and a worker with dialogue, such as `Events/Bs_Besaid/Bsvr0000/SprsHan039_33.cs`.

Each `Text[n]` is followed by a grey hint with its text on one line, the speaker first when the window names one. Hover over it to see the whole message, line by line. `Target.Wolf01` shows the monster's name, and `Map.Bsvr0000` the place's.

The language is the setting `atel.textLanguage`, `en` by default. The command **Atel#: Select the message language** switches it to another exported language.

## Rider

1. Download the `.zip` from the latest [GitHub release](https://github.com/yutamago/ffx-atel-sharp/releases).
2. In Rider, open **Settings**, then **Plugins**, the gear icon, and **Install Plugin from Disk…**. Pick the zip and restart Rider.
3. Open `atel-vanilla/Atel.slnx`, and a worker with dialogue.

The plugin shows the same hints. **Ctrl+Q** on a `Text[n]` shows the whole message. Right-click a hint to change the language or hide the hints. The settings are under **Settings**, **Tools**, **Atel# message text**.

The plugin is built for Rider 2026.3 only. A newer Rider needs a newer plugin.

## Your mod's project

Open your mod project as well as the vanilla source, or instead of it. The project names the vanilla source, so the editor knows the vanilla classes in your mod too.

The extensions look for the nearest `.atel` folder above the file they show, and your mod's folder is next to `atel-vanilla`, not inside it. For hints in your mod's files, point the extension at the export: in VS Code, set `atel.textDirectory` to `atel-vanilla/.atel/text` and `atel.namesDirectory` to `atel-vanilla/.atel/names`; in Rider, set the text and names folders in the plugin's settings.

## If no hints appear

- Check that `atel-vanilla/.atel/text/` exists. Without an export, the hover says there is none.
- In Rider, inlay hints must be on in **Settings**, **Editor**, **Inlay Hints**, and the file must be part of the open solution.
- In VS Code, the hints show for C# files only, outside `//` comments.

Visual Studio has no extension yet. It works for writing mods, without the text hints.

## Details

- [Message text and names in the editor](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/editor.md) (docs/editor.md)
- [The VS Code extension's settings](https://github.com/yutamago/ffx-atel-sharp/blob/main/editors/vscode/README.md) (editors/vscode/README.md)
- [The Rider plugin](https://github.com/yutamago/ffx-atel-sharp/blob/main/editors/rider/README.md) (editors/rider/README.md)
