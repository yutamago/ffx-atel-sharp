# Message text in the editor

Phase 8 of the [plan](plan.md): the source names messages by index (`Common.MsgSetText(0, Text[12]);`,
"Text"), and the editor shows what message 12 says, in a language you choose, from your own game. No
grammar, parser or language server of our own.

Three parts:

- `atel text export` writes the messages of every script per language as JSON beside the decompiled
  source;
- the VS Code extension (`editors/vscode`) shows them as inlay hint and hover;
- `atel decompile --text-comments <lang>` writes them as `// "…"` comments, for reading anywhere else.

Rider has no plugin yet (see "Rider").

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

Rider is an IntelliJ frontend (Kotlin/Java) over a ReSharper backend (C#), and C# is the backend's: its
PSI, inlay hints, and quick documentation live there. The options, cheapest first:

1. **`--text-comments`** (done): works in Rider and everywhere, no plugin; but the language is fixed when
   decompiling and the comments are in the source.
2. **A frontend-only plugin** (Kotlin, IntelliJ Platform Gradle Plugin, built against the installed Rider):
   a `DeclarativeInlayHintsProvider` registered for `language="C#"` (or any) that scans the document text
   with the same regex and reads the same JSON, the hint's tooltip holding the whole message. About a day
   with the build setup, about 200 lines. Unknown, so it needs a spike first: whether Rider's frontend runs
   frontend inlay providers on C# files at all (its C# PSI on the frontend is a stub, and C# hints come
   from the backend over the protocol). A hover is harder from the frontend, as C# quick documentation is
   the backend's; the tooltip of the hint would have to do.
3. **A backend plugin** (ReSharper SDK, C#, plus the frontend wrapper Rider needs; JetBrains' rider plugin
   template): an inlay hint highlighting from a daemon stage on `Text[n]` element access expressions,
   and a quick-doc or identifier tooltip provider for the hover. The robust way, and in C# like the rest
   of the tools, but 2-4 days: two builds (Gradle and dotnet), the SDK packages (several GB), and a release
   to follow every Rider version.
4. **An LSP server** for Rider's LSP API: excluded by the plan (no language server of our own), and it
   would compete with Rider's own C# support for the same files.

Decision: not implemented now; option 2's spike is the follow-up (if Rider's frontend runs the provider
on C# files, it is small), else option 3. Testing either means running Rider, which needs a session where
a window may open.
