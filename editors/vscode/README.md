# Atel# message text

Shows the text behind `Text[n]` in decompiled [Atel#](https://github.com/yutamago/ffx-atel-sharp) sources
(Final Fantasy X's scripts as C#): as an inlay hint after the index and as a hover, in the language you
choose. The text comes from your own copy of the game; the extension ships none.

```csharp
Common.MsgSetText(0, Text[3]);  "{name:Tidus} ⏎ Hey, wait! …"
Btl.UseCommand(Target.Biran "…", Command.Attack "…");
```

It also shows the game's name after the typed values that index the game's name tables, `Target.*`
(monsters), `Command.*` (commands, items, abilities), `Map.*` (the place) and `Battle.*` (the formation's
monsters), and casts of a constant to them (`(Command)0x3000`), from `atel names export`.

It complements the C# extension and has no language server: it reads the file's text.

## Setup

1. Decompile and export the message text from your game (`FFX_GAME_DIR` set to the folder with `FFX.exe`):

   ```bash
   atel decompile all -o src
   atel text export -o src                 # every language; or --lang en,de
   atel names export -o src                # the game's names, the same way
   ```

   `atel text export` writes `src/.atel/text/<lang>/<script>.json`. The source tree's `.gitignore` already
   ignores `.atel/`, so the text stays on your machine.

2. Install the extension: `code --install-extension atel-text.vsix` (built with `npm run package`).

3. Open `src` (or any folder in it) and a script's `.cs` file.

## Settings

| Setting | Default | |
|---|---|---|
| `atel.textLanguage` | `en` | `en`, `ja`, `fr`, `es`, `de`, `it`, `ko` or `zh` |
| `atel.textDirectory` | (empty) | the folder with the language folders; empty: the nearest `.atel/text` above the file |
| `atel.inlayHints.enabled` | `true` | hints on or off (the hover stays) |
| `atel.inlayHints.maxLength` | `60` | hints are cut here |
| `atel.inlayHints.names` | `true` | name hints on or off (the hover stays) |
| `atel.namesDirectory` | (empty) | `atel names export`'s folder; empty: the nearest `.atel/names` above the file |

## Which table

A file's script comes from the first line `atel decompile` writes (`// event/obj/…/bjyt0400.ebp, …`), else
the script attribute (`[Event("bjyt0400")]`), else the namespace (`Events.Bj.Bjyt0400`), else the folder. A
library class (`Library/Shared0001.cs`) is shared by workers of several scripts; its hints show the first
user's text with `(+n)` when others differ, and the hover lists them. Monsters have no table of their own.

Control codes are shown as markers: `{name:Tidus}` (a character's name), `{wait:4}`, `{color:41}`,
`{button:5}`, `{choice:0}`, `{var:1}`, `{item:0}`, `{keyitem:0}`, `{macro:6:0}`, `{space:3}`, `{tab:n}`,
`{break}`; codes without a known meaning as hex (`{2a:31}`).

## Building

```bash
npm ci
npm test          # compiles and runs the unit tests
npm run package   # atel-text.vsix
```
