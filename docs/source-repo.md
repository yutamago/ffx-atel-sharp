<!-- SPDX-License-Identifier: MIT -->
# The source repo

The decompiled source of every script lives in a private GitHub repo,
[`yutamago/ffx-atel-src`](https://github.com/yutamago/ffx-atel-src) (plan:
"Decisions", Phase 7). It is the game's code, decompiled from the player's
own `FFX_Data.vbf`: **it stays private**, and none of it goes into this repo.
This repo holds only the tool that writes it (`atel export`) and the check
(`atel compile-all`).

## Branches

| Branch | Written by | Holds |
|---|---|---|
| `generated` | `atel export` only | `atel decompile all`, one commit per export, the whole tree replaced (files the decompiler no longer writes disappear) |
| `main` | people, and `atel export`'s merge | `generated` merged in, plus hand edits: comments, reorganised code (names go into ffx-atel-sharp's `data/names/`, see "Names") |

A `generated` commit's message names what it was made from, without paths
or machine names:

```
Decompile with ffx-atel-sharp 925f5cd63598

atel decompile all, 19295 files.

ffx-atel-sharp: <commit> (<version>+<commit>)
FFX_Data.vbf sha256: <hex>
FFX.exe sha256: <hex>
```

The commit is the one the `atel` binary was built from (the SDK puts it in
the assembly's informational version), and the version is that commit's
(`0.1.0-alpha.0.<commits>.g<commit>`, [sdk.md](sdk.md), "Versions"), so
export from a committed checkout: uncommitted changes would get the
commit's version too.
Author and committer are the source repo's git identity (`user.name`,
`user.email`; Yutamago and the GitHub noreply address there). Git merges hand
edits with each new decompile as with any other branch.

## Names

Renaming a worker, an entry or a variable by hand on `main` would conflict with
every later decompile that touches the same lines, so names live in an
**overlay** that the decompiler applies (plan, "Decisions", 2026-11-02, changed
2026-11-03): one file per script under **this repo's** `data/names/`. They are
our own names keyed by numbers, with no game code or text, so they belong here,
and the decompiler names the same wherever it runs (`atel decompile`, `atel
export`, the SDK's vanilla source).

```
data/names/events/kami0000.toml
data/names/monsters/m209.toml
data/names/battles/kino00_00.toml
```

The folder is the script's category (`events`, `monsters`, `battles`), the
file its name in the game (`atel list`). The format is a small subset of TOML:
`#` comments, three sections, `key = "Name"` lines.

```toml
# kami0000

[workers]
18 = "LightningTowers"          # W18, by its number in [Workers]

[entries]
"18.2" = "StrikeNearPlayer"     # W18's entry 2

[variables]
"Event:0x0012" = "DodgeCount"   # [Var(Storage.Event, 0x0012)]
"Save:0x0a4a" = "ThunderFlags"  # also save variables
```

Every key is something the bytes keep from one decompile to the next, never a
name the decompiler made up:

- **Workers** by number: the class is renamed everywhere the script names it
  (`[Workers]`, the files, requests `LightningTowers.E5Async()`, `W18.Run()`).
  Its other workers keep their names, also those numbered because they share a
  model (`Kimahri_02`, `Kimahri_19`). A name may not be one the reference
  assembly or the script already uses, nor another worker's default name
  (`W03`).
- **Entries** by worker and entry number: the method that starts with the
  entry is renamed, `StrikeNearPlayer()`, or `StrikeNearPlayerAsync()` when
  other workers request it (write the name with or without `Async`; the
  requests are renamed too). `Init`, `Main` and a function worker's `Run`
  keep their names, as do entries that share a method with a lower one
  (`[Entry(2)][Entry(3)]`: name entry 2).
- **Variables** by storage and offset, as in their `[Var(Storage.X, offset)]`
  (the storage by its name or number, the offset in hex or decimal). A script
  that declares the same location twice gets the name and `Name_2`.

A name must be a C# identifier. A key the script does not have, or a name
given twice, stops the decompile with the file and the line. None of it
changes what the source compiles to: the attributes keep the numbers.

Renaming an entry changes the text of a worker's body, so a worker whose body
was shared with identical workers elsewhere (`: Shared0123`) then keeps its
own copy, with the new name; the library numbering of the others may shift by
one at the next export.

The files are embedded in the decompiler when it is built, so a change to
`data/names/` takes effect with the next build: commit it here (a PR, as any
change), build, then `atel export` the source repo. Check it first with
`atel roundtrip <script> --check`. `atel decompile` and `atel roundtrip` take
`--names <dir>` to use another folder laid out the same way instead (to try
names without rebuilding).

Without an overlay name, the decompiler names a worker after the model it loads
(`Tidus`, `TreasureChest_14`; `data/actor-names.json` gives the English names), or, an actor that
loads several, after the party member it binds itself to (`SetPartyIndex(k)`, 0 Tidus … 6 Rikku); in monster and
battle scripts, else after the battle's handler it serves (below); else after its role where
its code says it, read off the structured code by fixed rules (`WorkerNaming.AssignRoles`), in
this order:

| Name | Rule |
|---|---|
| `ExitTo<Map>` | an event trigger (`LineTrigger`, `BoxTrigger`, `LineSensor`, `BoxSensor`) whose map changes (`MapChange`, `MapJump`, `MapChangeDefault`, `ChangeMap`, `JumpToMap`) all go to one constant map: `ExitToKami0100` |
| `BattleLine`, `BattleBox`, `WarpLine`, `WarpBox` | an event trigger that starts a battle (`Btl.LabelExe`, `MsBattleLabelExe`) or jumps to an entrance of the same map (`MapJumpToEntrance`, no map change) |
| `Director` | an event worker whose `Main` switches on `StoryProgress` and requests other workers' entries from its cases (the story index on the script class) |
| `CameraLine`, `CameraBox` | an event trigger that only sets the camera (`Cam.*`, `AttachCamera`) besides its own setup (`TriggerSet*`, `Line*`, its target and yaw); line for types 2 and 5, box for 3 and 6 |
| `DialogueLine`, `DialogueBox` | the same, only message windows and voices (`Msg*`, `Voice*`, the player's control around them) |
| `MapSetup` | an event logic worker that sets the map's display up (`MinimapSet*`, `SetMapMarkerPos`, `GuideMapSetLimit`, `FootstepHookEnable`) and does nothing else but lights, screen, fades, music and waiting |
| `CameraWorker`, `DialogueWorker`, `Lighting` | any other worker that only drives the camera, only opens messages, or only sets the lights (`Screen.LightSet` and the skeleton refresh after it) |

Waiting (`Common.Wait*`) counts for none. A role several workers of a script share takes their
numbers (`CameraLine_21`, `CameraLine_25`), as model names do, so names stay unique and stable;
a name the script or the reference assembly already has is not taken. In the shipped scripts
(2026-11-05) this names 2,763 of the 11,326 workers that were `Wnn`; 8,563 keep their number.
The bases are the worker types' names (`Actor`, `LineTrigger`, …, [language.md](language.md#workers)).

**Monster and battle workers** are named after the handler of the file's handler table (section 1)
they serve: the signal the battle asks for, and each entry after the value `k` the table maps to it
(`HandlerNaming`; FFX.exe's `Mon_FindHandler` and its callers, [research/handlers.md](research/handlers.md)):

| Worker | Signal | Entries |
|---|---|---|
| `Ai` (monsters), `TidusActions`, … (battles, per party member), `YojimboSpecialActions`, … | the action request (`MsActionRequest`) | `OnTurn`, `OnTurnStart`, `OnTargeted`, `OnHit`, `OnDeath`, `OnEnter`, `OnTurnEnd`, `OnActionEnd`, `OnPoison` |
| `Cameras` | a battle camera | `OnCamera24` |
| `Motions`, `DefaultMotions` (monsters), `Motions`, `TidusMotions`, … (battles) | an actor's motion | `OnMotion39` |
| `CommandCameras`, `ItemCameras`, `MonsterCommandCameras`, `BossCommandCameras` | a command's camera | the command: `OnDelayAttack`, `OnPotion` |
| `SystemRequests`, `SystemRequests2` | a script's `Btl.AtelRequestSystemAllowCameras(k)` | `OnRequest0` |
| `BattleEvents`, `BattleEvents2` | the battle's start and end | `OnBattleStart`, `OnBattleEnd`, `OnPartyDefeated` |
| `PartyVoices`, `MonsterVoices`, `TidusVoices`, …; `TidusVoiceCues`, … | a voice line; a voice cue | `OnLine3`; the action names |

An entry the overlay names keeps the overlay's name; a name a worker already has is not given
twice. In the shipped scripts (2026-11-07) this names 891 of the 891 monster workers and 2,211 of
the 2,220 battle workers that were `Wnn`, and every entry method of a monster (2,023 that were
`En`) and all but 95 of a battle's (3,147).

**Function workers** are named after what they do to the worker that calls them
(`WorkerNaming.FunctionRole`): `SetMoveTalkDefaults`, `AttachEquipment`, `DetachEquipment`.

**Entries** the overlay and the handler table leave numbered (`E5`, `E5Async`) are named by
`EntryRoles`: first the trigger entries, from FFX.exe's default trigger table and the worker's own
`SetTriggerEntry` (`OnTalk`, `OnTouch`, `OnCross`, `OnTouchBegin`, `OnTouchEnd`; `OnTalk_6` for an
entry a worker points talking at), then what the method's code does (`Say12`, `Say12To15`,
`PlayVoice`, `MoveCamera`, `PlayMotion`, `Walk`, `Hide`, `Show`, `Unload`, `RunEffects`, `SetLights`,
`GoToKami0100`, `Scene12`, or the one native it calls). The rules and what was checked for each:
[research/variable-names.md](research/variable-names.md). A name several methods of a worker get
takes each one's entry (`Hide_7`); empty methods that are no trigger entry keep their number.

**Registers** (`[Register(0)] public static int I0;`) whose every store is one getter's result are
named after it without `Get` (`TurnKind`, `ActorMotionInfo`); the attribute keeps the number.

**Variables**: storage 1 (`Storage.Common`, shared by the battle and monster scripts of a battle)
by `data/variable-names.toml`, one name per offset for every script (`CameraActor`); a script's own
variables (storage 2, 3, 4, 6) by what the code does with them (`TriggerEntries`, a native's result:
`MsgChoiceResult`). The overlay's names come first.

Story progress values are not in the overlay: they are constants of the
reference assembly (`Story.S01340_Kami0400`), and their hand names go into
`data/story-names.json`, next to `data/names/`.

## Library names

Workers whose code is the same in several scripts derive from a library class
in `Library/` (plan, Phase 6, "Library base classes"). Their numbers,
`Shared0108`, follow the order the classes are first used in, which shifts
when a script's workers stop or start sharing, so a hand name cannot be keyed
by the number. It is keyed by the class's **signature**: a hash of its
workers' code (the worker type, the entry and label offsets, and each
instruction with constants by value and variables by location), which neither
the script it is in nor the decompiler's C# for it changes. The library file
shows it:

```csharp
// Signature 1e5a106de11263ab (the key of its name in ffx-atel-sharp's data/library-names.toml).
…
/// <summary>Attaches to camera 1 (AttachCamera(1, 0, 0)); …</summary>
public abstract class EventCamera : Worker1
```

The names are in this repo's `data/library-names.toml` (embedded like
`data/names/`, so a change takes effect with the next build):

```toml
[classes]
"1e5a106de11263ab" = "EventCamera"      # Shared0106, 75 users

[summaries]
"1e5a106de11263ab" = "Attaches to camera 1 (AttachCamera(1, 0, 0)); …"
```

A class not named there keeps its number, and named classes still take
theirs, so naming one renumbers nothing. A name says what the code does, read
off the natives it calls (their names are FFX.exe's), not a guessed role
(plan, "Decisions", "Only verified names"). Two classes can share a signature
when the same code is written differently in different scripts (an entry
another worker requests is `async Task`, elsewhere `void`): the one with more
users gets the name, the other `Name_2`. The corpus test checks that every
signature in the file still names a class.

A class without a hand name is named by rule, from its users (`SharedWorkers.AutoName`): where they
are (the one script, `Bjyt0000`, with the blitzball `cn_`/`psv_` copies counting as their script; else
the one area, `ThunderPlains`; test maps and other builds' copies count only when every user is one)
and what the decompiler named them all (their model, role or handler without the number:
`Bjyt0000CameraLine`, `ThunderPlainsSystemRequests2`), or `Idle` and the worker type for code that
does nothing. Without a place, a name a worker already has takes the category (`BattleSystemRequests2`).
A name taken twice gets `_2`, `_3`, … in order of first use. Classes whose users all kept their
number (`W17`) keep `SharedNNNN`.

**Bodies that name a sibling worker.** A worker whose body names other workers of its script
(`SetMoveTalkDefaults.Run()`, `W59.Say98Async()`, `Tidus.Pos`) is shared too when each of those is
the only worker of its script deriving from a library class: the library body names the class
(`MoveTalkDefaults.Run()`), and the compiler reads a library class's name as the one worker of the
script that derives from it (`ScriptCompiler.LibraryAliases`; a class two workers derive from, or
that is a worker's name, stands for nothing).

## Notes

The decompiler writes comments read off the code, so a reader can follow the
flow. Comments only: the source compiles to the same bytes.

- **Who starts an entry.** An entry method that other workers request (with
  a constant worker and entry) or call (a function worker's `Run`) says by
  whom:

  ```csharp
  /// <summary>Requested by W49.Main.</summary>
  [Entry(6)]
  public static async Task E6Async()
  ```

  A method that holds several entries lists them (`Entry 3: requested by …`).
  A worker that derives from a library class gets the list on its class line
  instead (`/// <summary>E2Async: requested by W00.Main.</summary>`), so the
  library stays shared. Requests whose worker or entry is computed at run
  time are not listed.
- **The story index** on the script class: every `switch (StoryProgress)` in
  a worker's `Init` or `Main` that requests entries, with its cases and the
  entries each requests (the first six, and how many more):

  ```csharp
  /// <summary>
  /// The story index: each switch (StoryProgress) in a worker's Init or Main that requests entries,
  /// with the entries each case requests (read off the code by atel).
  /// <code>
  /// W57.Main:
  ///   Story.S00200_Bsvr0200: -
  ///   Story.S00210_Bsvr0000 to Story.S00220_Bsil0100: SprsHan030.E6Async, SprsHan028.E6Async, … and 2 more
  ///   otherwise: …
  /// </code>
  /// </summary>
  [Event("bsvr0000")]
  ```

  It is on the script class rather than in a generated file per area: it
  travels with the script (also in `atel decompile bsvr0000`), the IDE shows
  it on hover, and the methods it names are in the same folder.

## Commands

From a build of this repo (`dotnet build -c Release src/Ffx.Atel.Cli`; the
binary is `artifacts/bin/Ffx.Atel.Cli/release/atel`), with `FFX_GAME_DIR`
set:

- **`atel compile-all <dir> [filter] [--check]`** compiles every script
  folder under `<dir>` (the top-most folders holding `.cs` files, outside
  `Library/`, `obj/`, `bin/` and dot folders) with the classes of
  `<dir>/Library/`, and compares each result with the game's file. It lists
  the scripts that differ, the game's scripts without source, and anything
  that does not compile; `--check` also compiles them as C# (Roslyn, against
  `Ffx.Atel.Game`), as `roundtrip --check` does. Exit code 0 only when
  every script of the game compiles to its bytes. A filter (`Events/Bj`)
  limits it to folders whose path contains it, and skips the "without
  source" list.
- **`atel export <repo> [--no-merge] [--no-compile] [--check] [--push]`**,
  with `<repo>` a clean checkout of the source repo on `main`:
  1. hashes `FFX_Data.vbf` and `FFX.exe` (SHA-256) while it decompiles every
     script into `<repo>/.git/atel-export/` (with this repo's `data/names/`
     applied), with the dialogue as comments (`Text[71] // "…"`, English;
     `--text-comments <lang>`, `--no-text-comments`), and writes the README;
  2. refuses to go on if any file names the user's home folder or the
     machine name;
  3. commits that tree onto `generated` through a separate index (`git add
     -A`, `write-tree`, `commit-tree`, `update-ref`): the working tree and
     `main` are not touched, and nothing is committed when the tree equals
     the last export's;
  4. merges `generated` into `main` (creating `main` on the first export).
     On a conflict it lists the conflicting files, leaves the merge in
     progress and stops;
  5. runs `compile-all` on `main`'s working tree;
  6. with `--push`, pushes both branches to `origin`, only when all of that
     passed.

Both take the machine-wide corpus lock (`GameData.OpenArchiveExclusive`,
`ffx-atel-corpus.lock` in the temp folder) as `roundtrip` and `decompile all`
do: each uses every core and several GB, so they wait for one another.
`dotnet build Atel.slnx` takes no lock; run it on its own too.

## What the tree holds

- `Events/<code>_<Area>/<Script>/`, `Battles/<code>_<Area>/<Script>/`,
  `Monsters/<Mnnn>/`: a folder and namespace per script (the area's English
  name from `data/areas.json`; plan, Phase 6, "Area folders").
- `AREAS.md`: per area its scripts with the game's place names, the named
  workers, notes on scripts the PC build does not load, and per battle its
  monsters.
- `Start.slnx` (the Thunder Plains and the monsters: open this first),
  `Atel.slnx` (everything but blitzball and the variants), `Events.slnx`,
  `Battles.slnx`, `Monsters.slnx`, `Blitzball.slnx` (only when an area's source is over 64 MB,
  each of its scripts its own project; since the blitzball scripts' repeated code is written once,
  2026-11-04, they are one area project of 57 MB and in `Atel.slnx`), `Variants.slnx` (see below),
  `.vscode/`.
- In each script folder, beside the script class and the workers:
  `<Script>.Layout.cs` (the header, pins and pools, and the workers'
  `[Worker(…)]`) and `<Script>.Data.cs` (the data areas and the workers'
  `[PrivateData]`), so that the other files read as code.
- **Variants**: scripts the PC build does not load or loads only behind a
  debug switch (the note at the top of their files, from `data/areas.json`:
  the `cn_`, `psv_`, `psvcn_` copies, `testpub0`, `200thunder_kami0400`,
  `dbg_nagi0000`, `full_nagi0700`), and the developers' test maps and
  battles (names with `test`, `sample*`). They stay in their folders, so
  their namespaces and the paths `compile-all` reads do not change, but
  each has a project of its own: an area of variants only is one project
  (`Events/Te/`, `Battles/Test/`); a variant in an area with other scripts
  is a project inside the area's folder
  (`Events/Ka_ThunderPlains/_200thunder_kami0400/`), which the area's
  project leaves out (`DefaultItemExcludes`). Only `Variants.slnx` holds
  them, blitzball's copies (`Cn_bltz0000`, whose `W36` is the 15.7 MB
  method) included.
- The dialogue as comments after each `Text[n]`, in English. Comments only:
  the round trip is unchanged.

## Workflows

**After a decompiler change** (new names, a new layout): commit it here,
build, then in the source repo

```bash
git switch main && git status            # clean
atel export . --push
```

A conflict means the new output changed lines that were edited by hand.
Resolve them (keep the hand edit, adapted to the new output), `git commit`,
`atel compile-all .`, `git push origin main generated`.

**Renames** go into this repo's `data/names/` (see "Names"): merge them
here, build, then `atel export . --push`.

**Other hand edits on `main`** (a comment, reorganised code): edit, then

```bash
atel compile-all . Events/Bj             # the scripts you touched
atel compile-all .                       # everything, before pushing
dotnet build Events.slnx                 # optional: the IDE's view
```

An edit that only renames or reorganises must leave the list of differing
scripts empty. An edit meant to change behaviour shows up as its script
differing: that is the list to read. Such edits belong in a mod rather than
in `main` (plan, "Mods"); `main` is the vanilla source.

**Never commit to `generated` by hand**: the next export would replace it
anyway, and the merge would then undo it on `main`.

## The reference assembly

The tree's projects use the SDK package, `<Project Sdk="AtelSharp.Sdk">`, of
the version in `global.json` ([sdk.md](sdk.md)), the version of the `atel`
that decompiled it: the reference assembly, the analyzers and the build
settings. `atel decompile -o` copies the package to
`.atel/packages/` and writes a `nuget.config` naming it when it finds the
package (beside `atel`, or in this repo's `artifacts/package/<configuration>/`
after `dotnet pack src/Ffx.Atel.Sdk`) or is given it (`--sdk <dir>`). The
tree's `.gitignore` ignores `.atel/` (a local decompile is not a repo).
`atel export` requires the package and commits it anyway (`git add -f`), so a
clone of the source repo opens and builds without a checkout of this repo,
against exactly the API the source was decompiled with. It is our MIT code,
not game data, about 4.4 MB per version (the `atel` CLI with Roslyn is most of
it).

`atel decompile --no-sdk` and `atel export --no-sdk` write the earlier layout:
`Ffx.Atel.Game.dll` and its XML docs in `.atel/`, which `Directory.Build.props`
references (about 1.2 MB, no analyzers). To build that against a checkout of
this repo instead:
`dotnet build Atel.slnx -p:AtelGameProject=<ffx-atel-sharp>/src/Ffx.Atel.Game/Ffx.Atel.Game.csproj`.

Every shipped project maps the checkout's path to `/_/` (`PathMap` in
`Directory.Build.props`), so no PDB path in the package or the DLL names a
user folder. `atel export` checks the tree for the home folder and the
machine name before it commits.

## Size

The first export (2026-11-02, ffx-atel-sharp `bb8700c`, `generated` and `main`
at `2fbaf85`): 19,373 files, about 405 MB; the largest is
`Events/Bl/Cn_bltz0000/W36.cs` at 15.7 MB, far below GitHub's 100 MB limit
per file (and its 50 MB warning). Packed, the repo is 15.6 MB. Git LFS is
not needed. On `main`, `atel compile-all .` gave 1,621 / 1,621 scripts
identical (42 s), and `dotnet build Atel.slnx -m:1
-p:UseSharedCompilation=false` 0 errors and 0 warnings (7.7 min; build one
project at a time: the blitzball projects take about 2 GB each).
