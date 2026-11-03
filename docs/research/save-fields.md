<!-- SPDX-License-Identifier: MIT -->
# Save fields we name

The `Save` class of the reference assembly (`src/Ffx.Atel.Game/Save.cs`)
names storage 0 offsets after Fahrenheit's `SaveData`. Fahrenheit has no name
for many of the fields the scripts use most; this page gives the evidence for
the ones we name ourselves (summaries marked "ours"). The rule is the plan's
"only verified names": a name goes in when the scripts say what the field is
(the dialogue shown next to it, or the only thing that writes and reads it),
not when it only looks likely. Offsets are storage 0 offsets (save offset
minus `0x1ec`).

The counts are from `atel decompile all` at ffx-atel-sharp `5bf314c`: uses of
the old `Save_xxxx` name across all 1,621 scripts (declarations included; the blitzball
scripts count many times, as there are several copies of each).

No engine function reads these offsets in ffx-rust's symbol file
(`g_SaveData + 0x1ec + offset` has no label or comment), so the evidence is
the scripts'.

## Named

| Offset | Name | Uses | Evidence |
|---|---|---|---|
| `0x0208` | `LightningDodgingRewards` (bits, `Save.LightningReward`) | 81 | The bolt workers of `kami0000` and `kami0300` set bit 0-5 when `LightningDodgingHighestConsecutiveDodges` reaches 5, 10, 20, 50, 100, 150 and bit 6, 7 when `LightningDodgingTotalBolts` reaches 30, 80 (200 in a row sets a bit of `ProgressionFlagsThunderPlains` instead). The chest of `kami0400` gives one reward per set bit, each with a message saying which: "dodged 5 in a row" … "150 in a row" for bits 0-5, and the two "struck by lightning" awards for bits 6 and 7. |
| `0x0216` | `LightningDodgingRewardsClaimed` (bits, the same) | 59 | The same chest sets the bit of the reward it gives and only gives rewards whose bit is clear here. |
| `0x0a4a` | `SaveSphereTipsShown` (bits, `Save.SaveSphereTip`) | 1,568 | Every save sphere worker (`Savesphere001`) shows the "Traveler's Save Sphere Level 1/2/3" message while bit 0/1/2 is clear and then sets it; at start it sets the bits of the levels the story has passed. |
| `0x0a60` | `AlBhedPrimersFound` | 313 | Every primer (`Albdbook011`, and `Breaksphere069`) adds 1 after "Obtained Al Bhed Primer vol. N", then picks the rank message ("achieved the rank of …") by its value. |
| `0x0a68` | `LastSaveSphereMap` (`u32`) | 642 | Every save sphere stores `GetNowEventJumpId()` here when used. The blitzball scripts leave the stadium with `MapChange(LastSaveSphereMap, LastSaveSphereEntrance)` (native `0x00ab`, `(map, entrance)` in FFX.exe), and the airship's scripts switch on it to set `CurrentAirshipLocation`. |
| `0x0a6c` | `LastSaveSphereEntrance` (`u32`) | 634 | Written by each save sphere with its own constant (1-8) next to the map; read only as `MapChange`'s entrance. |
| `0x0a88` | `BlitzballTeamSizes` (6 bytes) | 1,155 | The save sphere refuses the stadium with "the Aurochs are not at full strength, you need more members" while `[5] < 6`. The scouts loop `slot < BlitzballTeamSizes[team]` over `BlitzballTeamRosters[team, slot]`, signing a player writes `BlitzballTeamRosters[5, BlitzballTeamSizes[5]]` and adds 1, and a full team is 8. Team 5 is the Aurochs: the match result gives the Aurochs' score to whichever side is team 5. |
| `0x0a9a` | `AlBhedPrimerTutorialShown` | 111 | The first primer shows the "Introduction to Al Bhed" message only while it is 0, then sets 1. |
| `0x1000` | `BlitzballPlayerTechsLearnedPage1` (60 × `u32`) | 7,499 | The scout screen (scout level MAX) lists tech n by name when bit n of `[player]` is set, as unknown when it is only in `BlitzballPlayerUncoveredTechsPage1`, as blank otherwise. |
| `0x10f0` | `BlitzballPlayerTechsLearnedPage2` | 7,439 | The same list for techs 31-60 (bit n is tech n + 30). |
| `0x1266` | `BlitzballPlayerTechs` (60 × 5 bytes) | 20,299 | The scout screen (level 3 and MAX) shows `[player, i]` for `i < BlitzballPlayerTechSlots[player]` in the player's five tech lines. |
| `0x1392` | `BlitzballPlayerTechSlots` (60 bytes) | 8,595 | After a match the blitzball game sets it from the player's level: 0 below 3, 1 for 3-6, 2 for 7-11, 3 for 12-19, 4 for 20-29, 5 from 30. |
| `0x13ce` | `BlitzballPlayerLevels` (60 bytes) | 20,221 | The scout screen shows it in the "Level:" line, the stats are polynomials of it, a match raises it. |
| `0x141a` | `BlitzballTeamRosters` (6 × 8 bytes) | 30,570 | The scouts find a player's team by searching it (the team's name is message 70 + team), the release menu lists `[5, 0..7]` as the Aurochs' players. |
| `0x1452` | `BlitzballAurochsScore` | 507 | The scoreboard shows it (tens and ones), the result writes it to the Aurochs' side (`V90[team 5] = …`), and a win is counted when it is above `0x1453`. |
| `0x1453` | `BlitzballOpponentScore` | 514 | The other side of the same lines. |
| `0x152a` | `BlitzballPlayerContracts` (60 bytes) | 1,992 | The scout screen shows it in the "Contract: N games" line (255 shows a word instead, and the release menu says "Cannot release that player" for 255); signing sets it, and a match takes 1 off unless it is 255. |
| `0x1566` | `BlitzballAurochsWins` (`u16`) | 287 | A match the Aurochs win (`0x1452 > 0x1453`) adds 1, up to 65000; the result screen says "Besaid Aurochs are team level N (wins: M)" with N = wins / 5 + 1, and the scout level rises with it. |
| `0x1910` | `BlitzballPlayerUncoveredTechsPage2` | 1,403 | Fahrenheit's `blitzball_data.player_uncovered_techs_page_2`. Fahrenheit declares page 1 as 240 × `u32`, which would put page 2 at `0x1be0`; the scripts declare page 1 as 60 × `u32` (240 bytes) at `0x1820`, read page 2 at `0x1910` (page 1 + 240), and OR it into the scout screen's tech list for techs 31-60, as page 1 for techs 1-30. |

The per-script overlay (`data/names/`) names the dodge streak, the only
script variable here with a verified meaning: `kami0000` `Event:0x0012` and
`kami0300` `Event:0x000e`, `ConsecutiveDodges` (the bolt worker adds 1 per
dodged bolt, raises `LightningDodgingHighestConsecutiveDodges` to it, and
clears it when a bolt hits).

## Bits

Nested classes of `Save` name the bits, as `int` constants the compiler and
C# both read: `LightningDodgingRewards[0] & Save.LightningReward.Streak5`
compiles to the same bytes as `& 1` (checked on `kami0400`). `[Bits(typeof(…))]`
on the field's constant in `Save` names its class, and the decompiler writes
a mask of `&`, `|` or `^` on the field (`if (!!AsBool(SaveSphereTipsShown[0] &
Save.SaveSphereTip.Level3))`, `LightningDodgingRewards[0] |=
Save.LightningReward.Bolts30;`) as the constant that has its value. A mask
without a name stays a number: the dark aeon bytes' lower bits
(`CompletionFlagsDarkValefor[0] |= 2;`, `CompletionFlagsDarkYojimbo[0] & 1`),
and masks of several bits.

- `Save.LightningReward`: `Streak5` … `Streak150` (bits 0-5), `Bolts30`,
  `Bolts80` (bits 6, 7).
- `Save.SaveSphereTip`: `Level1`, `Level2`, `Level3`.
- `Save.DarkAeonFlag.Defeated` (bit 7) of the eight `CompletionFlagsDark*`
  bytes: each area's battle event sets it on its aeon (Besaid `bsvr0000`
  Valefor, Bikanel `bika0300` Ifrit, Thunder Plains `kami0300` Ixion,
  Macalania `mcyt0000` Shiva, Zanarkand `dome0600` Bahamut, Calm Lands
  `nagi0500` Yojimbo, Gagazet `mtgz0000` Anima, Mushroom Rock Road
  `kino0000`/`kino0100` Magus Sisters), and the event that finds all eight set
  sets `PenanceUnlockState` to 1.

## Names checked (P18)

The review found `CompletionFlagsDarkValefor` read by Wakka in Besaid. Every
use of it outside the developer test maps is in `bsvr0000` (Besaid village)
and in the eight-way Penance check: the party members test bit 7 (Dark
Valefor beaten) to change what they do in the village after the fight, and
the battle event sets bits 0-2 during the encounter. The engine reads the byte too
(`getInterValefor`, `g_SaveInterValefor` in FFX.exe; a map object draw tests
its bit 2, ffx-rust `docs/formats/field.md`). The name
is right; its summary now says what the bits are.

Every other Fahrenheit name was checked against where the scripts use it,
and they all fit: the `ProgressionFlags*` fields only in their own area, the
celestial fields at the celestial chests and the mirror, `AnimaSealsUnlocked`
at the destruction sphere chests of the six temples and Baaj,
`ProgressionFlagsEnergyBlast` where the Besaid dog brings Valefor's overdrive,
the lightning counters as the agency's record shows them. One size was
wrong and is fixed in the summary: `BlitzballPlayerUncoveredTechsPage1` is 60
`u32`s, not 240 (see `0x1910` above).

## Left unnamed (not verified)

- `0x0080` bit 0, `0x0099`: set when the party mounts a chocobo on the
  Highroad, Mushroom Rock Road and in Macalania (with
  `SetBattleStatus(0)`), cleared by the save sphere's airship teleport. Most
  likely "riding a chocobo" and the chocobo's state, but no message says so.
- `0x0a98`: set to 1 in a Djose temple event (`djyt0900`), cleared by the
  airship teleport, read by `djyt0000`/`djyt0800` to choose the fade colour.
- `0x0a49`: set when the Aurochs' wins pass 99; what it unlocks is not shown.
- `0x1455`, `0x14b4`-`0x14b6`, `0x1487`, `0x148d`, `0x146c`, `0x140a`,
  `0x151a`, `0x15e0`, `0x1568`, `0x1529`, `0x024c`: blitzball match and league
  state used only inside the blitzball scripts (`0x1529` counts formations
  learnt, `0x024c` changes next to the league and tournament prize indexes), not shown to
  the player with a label.
- `0x0119`-`0x0130` (Besaid), `0x0168` (Luca), `0x0153`/`0x0154` (Luca),
  `0x01c7` (Djose), `0x0201`/`0x0203` (the ships), `0x020a`-`0x020d`
  (Thunder Plains NPC conversations), `0x0088` (Highroad chocobos),
  `0x0b6a` (monsters): area flags and counters, one per NPC or event, that
  need a look per scene.
- `BattleDialogLinesSeen` (Fahrenheit's, 42 bytes): the battle scripts set
  its bits once a party member's battle line was said, which fits; but the
  scripts declare it as 120 bytes and Luca's events and monster `m152` use
  byte 36 bits 3-5 for the Luca dock battles, and Luca uses byte 42, past
  Fahrenheit's end. The name stays until the later bytes are known.
