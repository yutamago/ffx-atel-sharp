# Atel# message text for Rider

Shows the text behind `Text[n]` in decompiled [Atel#](https://github.com/yutamago/ffx-atel-sharp) sources
(Final Fantasy X's scripts as C#): as an inlay hint after the index and in the quick documentation, in the
language you choose. The text comes from your own copy of the game; the plugin ships none. It is the Rider
counterpart of the VS Code extension (`editors/vscode`); [docs/editor.md](../../docs/editor.md) has the
details.

```csharp
Common.MsgSetText(0, Text[3]);  "{name:Tidus} ⏎ Hey, wait! …"
```

## Setup

1. Decompile and export the message text from your game (`FFX_GAME_DIR` set to the folder with `FFX.exe`):

   ```bash
   atel decompile all -o src
   atel text export -o src                 # every language; or --lang en,de
   ```

2. Build the plugin (below) and install it: Rider, **Settings → Plugins → ⚙ → Install Plugin from Disk…**,
   `build/distributions/atel-text-0.1.0.zip`, restart.

3. Open `src/Atel.slnx` and a script's `.cs` file.

## Settings

**Settings → Tools → Atel# message text**:

| Setting | Default | |
|---|---|---|
| Language | `en` | `en`, `ja`, `fr`, `es`, `de`, `it`, `ko` or `zh` |
| Text folder | (empty) | the folder with the language folders; empty: the nearest `.atel/text` above the file |
| Show inlay hints | on | hints on or off (the quick documentation stays) |
| Shorten inlay hints to | 60 | characters |

## Building

JDK 21 and the .NET 10 SDK; Gradle and the Rider SDK are downloaded (to `~/.gradle`, `~/.nuget/packages`).

```bash
./gradlew check         # the backend tests (ReSharper test framework, headless)
./gradlew buildPlugin   # build/distributions/atel-text-<version>.zip
```

- `src/dotnet/Ffx.Atel.Rider`: the backend (C#): the inlay hints, the quick documentation, the settings.
- `src/dotnet/Ffx.Atel.Rider.Tests`: its tests, on made-up sources and text (`test/data`).
- `src/rider`: the frontend part (plugin.xml, the settings page's registration).

Built for Rider 2026.3 (`ProductVersion` in `gradle.properties`, `SdkVersion` in `src/dotnet/Plugin.props`).
