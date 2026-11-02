# Message text in the editor

Phase 8 of the [plan](plan.md): the source names messages by index (`Common.MsgSetText(0, Text[12]);`,
"Text"), and the editor shows what message 12 says, in a language you choose, from your own game. No
grammar, parser or language server of our own.

Four parts:

- `atel text export` writes the messages of every script per language as JSON beside the decompiled
  source;
- the VS Code extension (`editors/vscode`) shows them as inlay hint and hover;
- the Rider plugin (`editors/rider`) does the same in Rider;
- `atel decompile --text-comments <lang>` writes them as `// "…"` comments, for reading anywhere else.

## Where the messages are

From ffx-rust's `docs/formats/text.md` and `docs/formats/script.md`; the codec is a port of its
`crates/ffx-text` (`Ffx.Atel.Bytecode.Text`).

- **Events**: `Text[n]` is entry *n* of the table at the script's ctrl work `+0x34`
  (`Native_MsgSetText`, `Msg_Text` `0x0086bf30`), which `TKMallocAndReadEventMessage` (`0x0088cac0`) loads
  per language from `ffx_ps2/ffx/master/new_<lang>pc/event/obj_ps3/<xx>/<event>/<event>.bin` (`<xx>` the
  event's first two letters; `new_uspc` English, `new_depc` German, …). An entry whose offset is 0, and
  every entry of an event that has no table in the language, is the same entry of the `.ebp`'s section 1,
  the original Japanese table (`AtelEventSetUp`, `0x00872ef0`); the export shows that text (Japanese, as
  the game would). 317 of the 397 events have an English table, 366 a table of their own; 368 have
  messages in English one way or the other. Of the 29 with neither, only `memochek` (a developer map)
  calls a message native; it has no text anywhere. The Japanese folders also hold `.ftc` files beside
  the tables, which are not message tables.
- **Battles**: `new_<lang>pc/battle/btl/<name>/<name>.bin`, 163 of the 863 battle scripts. The export
  writes them too, but no battle script calls `MsgSetText` or `MsgGetText_0236`, so their sources have
  no `Text[n]`: battle texts are shown through the battle natives (`0x7042`, `0x7072`, `0x70b7`,
  `0x711f`/`0x7121`; the table at `*0x0112a9b8`, script.md), whose index parameters are plain `int`s so
  far. Typing those as `TextId` is a later Phase 5 step, once it is known which table each reads.
- **Monsters** have no table of their own and call no message native either (their names and texts are
  in the kernel `monster` tables). Should one ever use `Text[n]`, the index is into the running battle's
  table (the monster runs in the battle's ctrl work); the extension says so instead of guessing.

Measured over the corpus: all 33,759 `MsgSetText`/`MsgGetText_0236` calls are in 359 event scripts.

### Encoding

A message is FFX's byte encoding: characters by index into the language's charset
(`ffx_ps2/ffx/master/jppc/ffx_encoding/ffxsjistbl_<us|jp|kr|ch>.bin`, read from the player's archive),
line breaks (`03`), and control codes with or without an argument byte. `Charset.Decode` gives
ffx-rust's lossless form (`{0a:41}`, `{c:1a2}`, `{raw:2c}`, `{{`), which `Charset.Encode` turns back into
the same bytes: every message of every shipped table round-trips (`TextCorpusTests`). The editor shows
`Charset.Display`, which names what the message window does with each code (ffx-rust text.md, "Control
codes"):

| Bytes | Shown |
|---|---|
| `03` | a line break (`⏎` in a hint) |
| `01` | `{break}` |
| `07 e0+n` / `07 '2'` / `07 40+n` | `{space:n}` / `{right}` / `{tab:n}` |
| `09 '0'+n` / `09 c0+n` | `{wait:n}` / `{scroll:n}` |
| `0a n` | `{color:nn}` (hex) |
| `0b n` | `{button:n}` |
| `0e '0'+n` | `{icons:n}` |
| `10 '0'+n` / `10 50+n` | `{choice:n}` / `{counter:n+1}` |
| `12 '0'+n` | `{var:n}` (a value the script sets) |
| `13 '0'+n` | `{name:Tidus}`: the name of character *n* (the default names; the game shows the player's) |
| `16`, `17 '0'+n` | `{item:n}` |
| `19`-`22 '0'+n` | `{macro:s:n}`, *s* = code − `0x13` (a dictionary section) |
| `23 '0'+n` | `{keyitem:n}` |
| anything else | the code in hex: `{02}`, `{2a:31}` |

## `atel text export`

```bash
atel decompile all -o src
atel text export -o src                  # every language
atel text export -o src --lang en,de     # en, ja, fr, es, de, it, ko, zh (or the game's us, jp, sp, …)
atel text export bjyt0400 -o src         # some scripts
```

It writes `src/.atel/text/<lang>/<script>.json`:

```json
{
  "script": "bjyt0400",
  "category": "event",
  "language": "en",
  "messages": [ "…", "…", null ]
}
```

a message's index is its position, `null` a message without text. Without `-o` it uses the current
folder if it has `Atel.slnx`. With every script and all eight languages it takes the corpus lock
(`GameData.OpenArchiveExclusive`); it reads only the tables and the event files, no decompiling.

**It stays on the player's machine.** The files are game text, so they never go into this repository
(tests use made-up bytes). They don't go into the private source repo either: the decompiled tree's
`.gitignore` already ignores `.atel/` (where the reference assembly copy lives), and `atel export` builds
the `generated` branch in a fresh folder that holds no text. The text would be allowed there (it is game
data of the same kind as the decompiled source), but nothing needs it versioned: it is regenerated from
the game in seconds, for whichever languages the reader wants, and nobody edits it there (translations
belong to mods, in the per-language TOML of "Text" in the plan).

## VS Code

`editors/vscode`, TypeScript, about 300 lines; the [README](../editors/vscode/README.md) has setup and
settings. It registers an inlay hints provider and a hover provider for C# files and reads the file's
text with a regex: `Text[12]` outside a `//` comment.

- The script: the first line `atel decompile` writes (`// event/obj/bj/bjyt0400/bjyt0400.ebp, decompiled
  by atel.`), else the script attribute (`[Event("bjyt0400")]`), else the namespace
  (`Events.Bj.Bjyt0400`), else the folder. A library class (`Library/Shared0001.cs`, Phase 6) is shared
  by workers of several scripts: the hint shows its first user's message (`(+n)` when the others differ)
  and the hover lists them.
- The text: `<nearest .atel/text above the file>/<atel.textLanguage>/<script>.json`, or under
  `atel.textDirectory`. Changes to the JSON files and to the settings refresh the hints.
- Settings: `atel.textLanguage` (`en` …), `atel.textDirectory`, `atel.inlayHints.enabled`,
  `atel.inlayHints.maxLength`.

Checks:

- `npm test`: unit tests of the resolution (`src/resolve.ts`) with `node --test`, made-up names;
- `npm run test:integration`: starts VS Code through `@vscode/test-electron` (downloaded into
  `.vscode-test`, or `VSCODE_EXECUTABLE`) on a made-up source tree with a made-up text file, and checks
  the inlay hint and the hover through `vscode.executeInlayHintProvider` and `vscode.executeHoverProvider`,
  in English and, after changing `atel.textLanguage`, German. On Linux under `xvfb-run -a`;
- `npm run package` writes `atel-text.vsix`; `code --install-extension atel-text.vsix` installs it. CI builds
  and packages it (no publishing).

By hand: decompile a script and export its text, install the `.vsix`, open the source folder and a
worker file with `Text[n]` (`Events/Bj/Bjyt0400/W17.cs`); the message follows each index, hovering shows
it whole; switch `atel.textLanguage` to `de` and the hints change.

## `atel decompile --text-comments <lang>`

The plan's formatter option: a line that uses `Text[n]` ends with the message as a C# comment,

```csharp
Common.MsgSetText(0, Text[3]); // "{name:Tidus}\nHey, …"
```

several on one line as `// 3: "…"; 4: "…"`. Comments only, so the source compiles to the same bytes
(`TextCommentTests`: four events and two battles, decompiled with English comments, compile back to the
game's files; 151 of 151 uses got their comment). Not the default: the editor shows the text in any
language without changing the source, and comments would make `atel export`'s `generated` branch
depend on a language. Library classes get none (their text depends on the script).

## Rider

`editors/rider`, a Rider plugin from JetBrains' Rider plugin template (IntelliJ Platform Gradle Plugin 2.x),
built against Rider 2026.3 (the SDK `2026.3-EAP8-SNAPSHOT` / `JetBrains.Rider.SDK 2026.3.0-eap08`; since-build
`263`, until-build `263.*`). The [README](../editors/rider/README.md) has building and settings.

### What it does

The same as the VS Code extension, from the same `.atel/text/<lang>/<script>.json` files and with the same
resolution rules (a C# port of `resolve.ts`):

- an **inlay hint** after each `Text[n]` with the message on one line (`"{name:Tidus} ⏎ Hey, …"`), cut at
  the configured length; a library class shows its first user's message, `(+n)` when the others differ.
  Hovering the hint shows the whole message;
- **quick documentation** on `Text[n]` (Ctrl+Q, and Rider's documentation on mouse hover): the whole
  message of each script the file belongs to, or why there is none (no export found, no text, no message
  *n* in the language, a monster);
- **Settings → Tools → Atel# message text**: the language (`en` … `zh`), the text folder (empty: the nearest
  `.atel/text` above the file; relative paths are from the solution's folder), hints on or off, their
  length. The settings are ReSharper settings (saved per machine by default; per solution in its
  `.DotSettings` layers if wanted). A new export or another language's files refresh the hints by
  themselves (a file watcher on the text folder).

`Text[n]` is found in the C# PSI, not by a regex: an element access on something named `Text` with a decimal
constant index (`Text[12]`, `x.Text[12]`), so comments and strings never match.

### How it is built

The route was chosen after a spike: Rider's C# lives in the ReSharper backend, so the plugin is a backend
plugin, as option 3 below.

- **Backend** (`src/dotnet/Ffx.Atel.Rider`, C#, .NET Framework 4.7.2 against `JetBrains.Rider.SDK`): a
  daemon `ElementProblemAnalyzer` on element access expressions adds the hint highlighting (an inlay-hint
  highlighting with an adornment provider, as Rider's own hints), an `IQuickDocProvider` (asked before the
  C# ones, which would document the `Text` indexer) gives the documentation, `AtelTextSettings` and an
  options page the settings.
- **Frontend** (`src/rider`, plugin.xml and one Kotlin class): the plugin descriptor and the registration of
  the backend's options page under Tools. No protocol model of our own: hints, documentation and settings go
  over the protocol Rider already has, so the template's `protocol` (rdgen) module is left out.
- **Tests** (`src/dotnet/Ffx.Atel.Rider.Tests`, the ReSharper test framework, headless): the daemon on
  made-up sources and tables (hints, their labels and tooltips: event, attribute, library class with
  `(+1)`, German with a short length, hints off), quick documentation through a test text control (an
  event, a library class, no export, not on `Text[n]`) and the resolution rules (the cases of
  `resolve.test.ts`). They run under `dotnet test` on Linux, macOS and Windows: the template targets
  Windows (net472 under NUnit with STA threads), so `HeadlessTests.targets` sets up what Rider's own
  backend uses off Windows (JetBrains' NUnit build with emulated STA threads, the Unix WPF stub, the
  .NET Core builds of the SDK's assemblies). The test framework downloads the reference assemblies of its
  test projects from JetBrains' platform feed (`test/data/nuget.config`) into `~/.local/share/JetBrains`.

Checks: `./gradlew check` (the backend tests) and `./gradlew buildPlugin` (the zip,
`build/distributions/atel-text-<version>.zip`); CI's `rider` job runs both on Ubuntu and keeps the zip as an
artifact. Nothing is published. Neither starts Rider: `buildSearchableOptions` (which would start the IDE
headless to index the settings page) is off.

### Installing

1. Build the zip (`./gradlew buildPlugin` in `editors/rider`; JDK 21 and the .NET 10 SDK), or take it from a
   CI run's `rider-plugin` artifact.
2. Rider: **Settings → Plugins → ⚙ → Install Plugin from Disk…**, choose
   `atel-text-<version>.zip`, restart Rider.
3. Export the text (`atel text export -o src`) and open the decompiled solution (`src/Atel.slnx`).

The plugin is built for Rider 2026.3 only (until-build `263.*`): the ReSharper API changes with every
release, so a newer Rider needs `ProductVersion` and `SdkVersion` raised and a rebuild.

### By hand

What the automated tests can't show is Rider's UI drawing the hints and the documentation popup. A short
check, after installing the zip:

1. Decompile a script and export its text: `atel decompile bjyt0400 -o src`, `atel text export bjyt0400 -o
   src --lang en,de`. Open `src/Atel.slnx` in Rider.
2. Open a worker with `Text[n]` (`Events/Bj/Bjyt0400/W17.cs`): each `Text[n]` is followed by a grey hint with
   the message in quotes; long ones end in `…`. Hovering the hint shows the whole message.
3. Put the caret on a `Text[n]` and press Ctrl+Q (or hover it with "Show quick documentation on mouse move"
   on): the popup shows `Message n` and the text.
4. Settings → Tools → Atel# message text: Language `de`, OK. The hints switch to German without reopening
   the file. Uncheck the hints: they go; the documentation stays.
5. Open a library class (`Library/Shared0001.cs`) that uses `Text[n]`: hints show the first user's message,
   `(+n)` where users differ; Ctrl+Q lists each user.

If no hints appear: Settings → Editor → Inlay Hints must have hints enabled at all, and the file must be in
the solution (the daemon only runs on solution files).

### The options, as written down before

Rider is an IntelliJ frontend (Kotlin/Java) over a ReSharper backend (C#), and C# is the backend's: its
PSI, inlay hints, and quick documentation live there. The options, cheapest first:

1. **`--text-comments`** (done): works in Rider and everywhere, no plugin; but the language is fixed when
   decompiling and the comments are in the source.
2. **A frontend-only plugin** (Kotlin): a `DeclarativeInlayHintsProvider` for C# files scanning the text with
   the same regex. Not taken: Rider's frontend C# PSI is a stub and C# hints come from the backend, and a
   hover from the frontend would have had to be the hint's tooltip.
3. **A backend plugin** (ReSharper SDK, C#, with the frontend part Rider needs): taken (above).
4. **An LSP server** for Rider's LSP API: excluded by the plan (no language server of our own), and it
   would compete with Rider's own C# support for the same files.
