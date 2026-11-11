<!-- SPDX-License-Identifier: MIT -->
# The Besaid course: example mods

The example mods of the website's Besaid course (docs/website.md, "Guides: the Besaid course"): one mod per guide,
all on the island of Besaid, which the game reaches about an hour and a half in. Each folder is a mod project of
the Atel SDK, built the way [docs/sdk.md](../../docs/sdk.md) describes.

## What they need

- **FFX only** (`FFX.exe`) from the Steam *FINAL FANTASY X/X-2 HD Remaster*. Atel mods change FFX's scripts; they
  do nothing in X-2 or Last Mission.
- **Fahrenheit** with the Atel loader (`yuta.atel`); tested with Fahrenheit 1.0.0-alpha12.
- **`AtelSharp.Sdk` 0.1.0-alpha.1** or later, and the .NET 10 SDK, to build them.

## Copying one

Each mod id is `your-name.<mod>`, and `Authors` is `Your Name`. A Fahrenheit mod id starts with its author, so put
your own name or handle in both before you build a copy. `BesaidTallyReader` names `your-name.besaidtally` in four
places (its `.csproj`, its `mod.toml` twice and its `[Saved(Mod = …)]`): change them with the tally's id.

Only `BesaidTallyReader` has a `mod.toml`: the file is optional, and the others have nothing to put in it
(`requires` and `after`, [docs/formats/mod-package.md](../../docs/formats/mod-package.md)).

| Mod | What you see | Script | Worker, entry | Shows |
|---|---|---|---|---|
| [GiantWakka](GiantWakka) | Wakka at twice his size in the village at night | `bsvr0001` | `Wakka`, `[Postfix]` on `Init` | changing an actor when a map loads |
| [ToughDingo](ToughDingo) | the Dingo with 1,250 HP, and 999 gil each | `m009` | static constructor: `MonsterStats`, `MonsterRewards` | data edits |
| [BesaidGossip](BesaidGossip) | an old woman's new line of dialogue | `bsvr0000` | `SprsHan039_33`, `[Postfix]` on `OnTalk` | `Text.New`, `Dialogue.Say` |
| [DancingVillager](DancingVillager) | the old woman dances after you talk to her | `bsvr0000` | `SprsHan039_33`, `[Postfix]` on `OnTalk` | a motion, waiting for it |
| [BeachQuake](BeachQuake) | the camera shakes as Tidus arrives on the beach | `bsil0000` | `W19`, `[Inject]` into `Main` | `Camera.Eye.Shake` |
| [FastTidus](FastTidus) | Tidus runs twice as fast on the Village Slope | `bsil0700` | `Tidus`, `[Postfix]` on `Init` | the player's speed |
| [GenerousVillager](GenerousVillager) | the old woman gives 1,000 gil, once per save | `bsvr0000` | `SprsHan039_33`, `[Postfix]` on `OnTalk` | a `[Saved]` field |
| [CleverFlan](CleverFlan) | the Water Flan heals itself below half HP | `m016` | `Ai`, `[Inject]` into `OnTurn` | code inside a monster's turn |
| [BesaidTally](BesaidTally) | the F6 overlay counts the Besaid monsters you defeat, kept in the save | `m009`, `m016`, `m028` | `Ai`, `[Postfix]` on `OnDeath` | a `[Saved]` field across scripts |
| [BesaidTallyReader](BesaidTallyReader) | the overlay shows that count when a Dingo appears | `m009` | `Ai`, `[Postfix]` on `Init` | reading another mod's saved field |
| [WhereAmI](WhereAmI) | the F6 overlay shows the story progress and Tidus's position, live | `bsvr0000` | a new worker `WhereAmI`, its `Main` | `Debug.Print`, a new worker |

`tests/Ffx.Atel.Sdk.Tests` (`ExampleModTests`) builds every one of them with the packed SDK and compares its
package with what `atel compile-mod` writes, when the game is there (`FFX_GAME_DIR`); CI has no game and skips it.

## Building them

From a folder outside this repository (decompiled source is the game's code: keep it on your machine), with
`atel` and the SDK feed as in [docs/tutorial.md](../../docs/tutorial.md), steps 2 and 3:

```bash
atel decompile bsvr0000 bsvr0001 bsil0000 bsil0700 m009 m016 m028 -o atel-vanilla --sdk ~/atel-feed
export FFX_GAME_DIR=<the folder with FFX.exe>
dotnet build <this repo>/examples/Besaid/GiantWakka -p:AtelVanillaSource=$PWD/atel-vanilla
```

The SDK's version goes in a `global.json` next to the mod or above it ([docs/sdk.md](../../docs/sdk.md),
"2. Create the project"). Without `-p:AtelVanillaSource`, each project looks for `atel-vanilla` next to the
repository's folder. The build writes the mod folder to `bin/Debug/net10.0/mods/<id>/`; install it as
[docs/tutorial.md](../../docs/tutorial.md), "6. Install", says, with its id in `loadorder` after `yuta.atel`.

The mods can be installed together. `BesaidGossip`, `DancingVillager` and `GenerousVillager` all add to the
same old woman's `OnTalk`: their parts run one after the other, in load order. `BesaidTallyReader` needs
`BesaidTally` before it in `loadorder`.

## The places

- **Besaid village** (`bsvr0000`) from the moment Tidus arrives with Wakka on, and on every later visit.
  Tidus and Wakka's arrival is another script (`bsvr0002`), and so is the night after the trial (`bsvr0001`).
- **The old woman** (`SprsHan039_33`, model `SprsHan039`) walks a long loop through the village. Talk to her as
  you catch up with her. On the first visit she says one of the statues in the temple looks like her husband.
  She is in the village at every visit, except on the morning Tidus wakes up in Wakka's tent and on the morning
  of the farewell scene.
- **The night after the trial** (`bsvr0001`): after Yuna gets Valefor in the temple, Tidus walks out into the
  village at night. Wakka stands in the square until Tidus walks on to the celebration.
- **The beach** (`bsil0000`, "Besaid - Port; Besaid - Beach") is where Tidus swims ashore, until the morning
  the party leaves the island. From then on, the beach is another script (`bsil0100`), where `BeachQuake` does
  nothing.
- **The Village Slope** (`bsil0700`) is the path just outside the village gate.
- **Dingos, Water Flans and Condors** are the random battles on the Village Slope (`bsil07_xx`) and the
  Waterfall Way (`bsil05_xx`). The tutorial battles there (Lulu's and Wakka's) may use the game's other copies of
  these monsters (`m230` to `m233`), which these mods do not change.

## In-game checks

Each check: where to go, what to do, what should happen, and what only the game can tell. Keep F6's overlay on
for the mods that print (it is on by default).

### GiantWakka

- **Where:** the night after the trial in Besaid village (`bsvr0001`). A save in the temple before or during
  the Cloister of Trials gets you there.
- **Do:** finish the trial and walk out of the temple.
- **Expect:** Wakka, standing in the village square, is twice as tall and twice as wide as everyone else.
- **Uncertain:** whether `Chr.SetScaleAll(2)` in `Init` holds once his motion starts (the vanilla scripts call it
  right after the model loads, as here); whether his shadow and the camera framing look right; whether he now
  blocks the path (his collision radius is not scaled).

### ToughDingo

- **Where:** a random battle with a Dingo on the Village Slope or the Waterfall Way (or later, from the
  airship).
- **Expect:** a Dingo takes about ten times as many hits (1,250 HP instead of 125) and hits harder (strength 30
  instead of 13). Each Dingo adds 999 gil on the victory screen, instead of 16. Nobody has Scan this early; with
  a later save and Scan or Sensor, the HP shows 1250.
- **Log:** `info: your-name.toughdingo: m009: sets MonsterStats.Hp (the monster's stats +0x14, 4 byte(s))`, and the
  same for `Strength` and `Gil`.
- **Uncertain:** whether the tutorial battles use `m009` too.

### BesaidGossip

- **Where:** Besaid village (`bsvr0000`), any visit from the arrival on.
- **Do:** talk to the old woman.
- **Expect:** after her own line, a second window: "They say Wakka once kicked a blitzball all the way to Kilika,
  ya?". It closes with the confirm button; then you can walk again.
- **Log:** `info: your-name.besaidgossip: bsvr0000: new message 316: …` (the number may differ with other mods).
- **Uncertain:** whether the new window looks like hers (same position and style: `Dialogue.Say`'s defaults,
  which most of the game's lines use). Her mouth does not move on the new line (the vanilla lines set
  `AutoTalk`; this one does not).

### DancingVillager

- **Where / do:** as for BesaidGossip.
- **Expect:** after the conversation she plays the motion her model plays at the celebration on the night
  after the trial (`0x60111008`), once, then walks on.
- **Uncertain:** whether that motion is loaded in the village at that time (her `Init` loads the same motion
  group, 24593, as the celebration's script does); what it looks like (cheering or dancing); whether she turns
  back to her path first (the vanilla `OnTalk` turns her before the postfix runs). If nothing plays, try
  `0x60111009` or `0x6011100a`, the celebration's other two.

### BeachQuake

- **Where:** the beach (`bsil0000`), before the party leaves the island.
- **Do:** walk from the Crossroads down to the beach, or load a save made at the beach's save sphere.
- **Expect:** as the screen fades in and Tidus can move, the camera shakes on all three axes for three seconds,
  fading out.
- **Log:** `info: your-name.beachquake: bsil0000: worker 19's Main: inject Quake, after the statement with the 1st
  call to Common.ControlEnable in Main`.
- **Uncertain:** whether a camera line Tidus crosses as he arrives resets the shake (the island's camera
  scripts call `Camera.Eye.ResetShake`); how strong amplitude 20 looks (the values are from a shake the game
  uses elsewhere: `Camera.Eye.Shake(ShakeKind.SineFadeOut, 7, 3.14f, 90, 20, 0)`).

### FastTidus

- **Where:** the Village Slope (`bsil0700`), any time.
- **Do:** leave the village by its gate and run.
- **Expect:** Tidus runs twice as fast.
- **Why it works:** kind 2 of `Screen.PlayerSetSpeed` is the run speed, which loading a map resets to 38. The
  beach (`bsil0100`) speeds Tidus up the same way, from his `Init` (`Screen.PlayerSetSpeed(2, 40.8f)`).
- **Uncertain:** that the next map is back to normal speed (it should be: each map load resets the speed).

### GenerousVillager

- **Where / do:** as for BesaidGossip.
- **Expect:** the first time you talk to her in a save, after her line: "Here, take 1,000 gil, ya? Buy yourself
  something nice.", and the gil in the menu is 1,000 higher. The second time, nothing more. Save, load the save:
  still nothing. Load a save made before: she gives the gil again.
- **Log:** `info: your-name.generousvillager: 1 saved field(s), 1 bytes at storage 0 …`.
- **Uncertain:** whether `Common.GiveGil` (native `0x0136`, which the blitzball scouting scripts use to give
  money back) shows any message of its own; whether the gil appears at once in the menu.

### CleverFlan

- **Where:** a random battle with a Water Flan on the Village Slope or the Waterfall Way.
- **Do:** bring its HP below half (it has 315; Lulu's Thunder works best, physical attacks barely hurt it), then
  let it take a turn.
- **Expect:** instead of attacking a party member, it casts Water on itself and heals (Water Flans absorb
  water). Above half HP it attacks as before.
- **Log:** `info: your-name.cleverflan: m016: worker 1's OnTurn: inject HealItself, after the statement with the 1st
  call to Btl.SelectTarget in OnTurn`.
- **Uncertain:** whether `Btl.UseCommand(Target.Own, …)` aims at the flan itself, and whether its Water heals
  it (if it does not absorb water, it hurts itself instead).

### BesaidTally and BesaidTallyReader

- **Where:** random battles on the Village Slope or the Waterfall Way.
- **Do:** defeat Dingos, Water Flans and Condors. Save, quit to the title, load, fight again.
- **Expect:** each defeat prints `Besaid monsters defeated: N` on the overlay, N one higher each time, and the
  count goes on after loading the save. With BesaidTallyReader, each Dingo that appears prints
  `Besaid Tally Reader: N defeated so far`.
- **Log:** `info: your-name.besaidtally: m009: worker 1's OnDeath: postfix Count`, and the same for `m016` and `m028`.
- **Uncertain:** whether `OnDeath` runs once per defeated monster in every case (fled battles, an aeon's
  finishing blow).
- **Why not battles:** the plan said "counts Besaid battles". A battle count needs a patch in each formation's
  script (a dozen, `bsil05_xx` and `bsil07_xx`); the monster scripts are shared by all of them, so the mod
  counts defeated monsters instead.

### WhereAmI

- **Where:** Besaid village (`bsvr0000`).
- **Expect:** once a second, a line on the overlay: `bsvr0000, story 134: Tidus at x 35, y -8, z 419`, the
  numbers changing as you move. Shift+F6 shows more lines. Leaving the village stops it.
- **Log:** `info: your-name.whereami: bsvr0000: new worker WhereAmI is worker 87`, then `Atel debug: worker 87: …`
  each second.
- **Uncertain:** whether the game starts a new worker's `Main` with the map's own workers (the linker adds it to
  the worker table).

## A dead block in Besaid

None worth switching on. `atel dead` finds two `if (false)` blocks in the Besaid scripts, both empty, in the
camera handlers of the battle with Kimahri (`bsil04_00`); none in the island and village events, nor in the
Dingo, Water Flan and Condor scripts. The guide on dead code needs a block elsewhere, such as
`Cut.ZanarkandCrowdFacingReaction` ([docs/sdk.md](../../docs/sdk.md), "Switching dead code on").
