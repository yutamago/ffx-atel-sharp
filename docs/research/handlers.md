# The battle's handlers: which worker runs for which event

Monster and battle scripts have no story index and no triggers. The battle runs their workers
through a table in the file: on a turn, a hit, a death, a camera or a motion it asks for a
**signal**, and the table says which worker, and which of its entries, handle it. This note
maps that table and the signals from FFX.exe (Steam HD Remaster, sha256 `0537b2a1…686da686d`).
Names and addresses are ffx-rust's (`symbols/ffx.sym`; the ports in `crates/ffx-battle/src/atel.rs`,
`action.rs`, `minor.rs`), and the call sites were read from FFX.exe's code (2026-11-07). The decompiler
names workers and entries from it (`HandlerNaming`; [source-repo.md](../source-repo.md#names)).
`atel handlers [filter]` prints every file's table.

## The table

Section 1 of a monster file (`battle/mon/_mNNN/mNNN.bin`, actor `+0xf7c`, `Mon_AttachData`
`0x00783b00`) and of a battle file (`battle/btl/*/*.bin`, the second script record's `+0x10`,
`MsBtlReadSetScene` `0x00783e20`). `Mon_FindHandler(file, n, &k)` (`0x00797410`) reads it:

| Offset | Type | Meaning |
|---|---|---|
| `0x00` | `u8` | the script's work count: the battle numbers the next script's works after these (actor `+0xdf6`, record `+0`) |
| `0x01` | `u8` | the signal count `N` |
| `0x02` | `u8` × `N` | per signal its record's slot, `0xff` none |
| halfword `(N + 1) / 2` | 4 bytes per slot | `+0` the worker, `+1` the handler's kind (below), `+2` an `i16` offset from the section to its entry list |
| list | `u16` count, `u16` × count | per value `k` the entry the worker runs, `0xffff` none |

`MsAtelRequestExe(kind, i, n, …)` (`0x00797d50`) walks the requests queued with
`MsAtelRequestAdd(kind, owner, n, k)` (`0x00797b70`) and runs the first whose own table has an entry
for its `k`: a request (`AtelRegistSignal2`) to that worker, at level 7, for that entry. `kind` is 2
for the battle's two scripts (owner 1 the battle's file; owner 0 the script registered first,
whose per-actor signals are the ones `system_01` has), 3 for an actor's monster file, 4 for the
script camera. The worker byte counts from the owner's first work (`MsAtelGetHandler`,
`0x00798590`). After an action or a battle event, `MsAtelParseSysTag` (`0x007979d0`) runs the
worker until it drops below level 7, so those finish before the battle goes on.

In every shipped file each worker handles at most one signal, and each entry at most one `k`; 2,385
of the 2,388 battle workers and all 898 monster workers are in a handler. The kind byte agrees
with the signal everywhere: 0 cameras, 1 motions, 2 actions, 3 voices, 4 system requests, 5 voice
cues, 6 battle events, 7 to 10 the four command cameras.

## Signals

| Signal | In | Kind | Asked for by | `k` | Worker name |
|---|---|---|---|---|---|
| `0x00` | both | 0 | `MsAtelRequestCamera` (`0x00797bc0`): every battle camera request, battle file, then the actor's monster file, then `system_01` | the camera number | `Cameras` |
| `0x03` | battle | 1 | `MsSetMotion` (`0x007ab360`) after the actor's own handler | the motion | `Motions` |
| `0x05` + i | battle | 1 | `MsSetMotion` for party member i (0 Tidus … 0x11 Magus3) | the motion | `TidusMotions`, … |
| `0x25` + i | battle | 2 | `MsActionRequest` with `special` for Yojimbo and the Magus sisters (i 0 to 3) | the action (below) | `YojimboSpecialActions`, … |
| `0x29` + i | battle | 2 | `MsActionRequest` (`0x007aceb0`) for party member i | the action | `TidusActions`, … |
| `0x3d` | monster | 2 | `MsActionRequest` for a monster | the action | `Ai` |
| `0x3e` | battle | 4 | `MsAtelRequestSystem` (`0x007981e0`, native `0x7097`) for `k` below 8 | the native's argument | `SystemRequests` |
| `0x3f` | battle | 4 | the same for `k` 8 to 0x17 (also queued for `system_01` with the voice event on) | the native's argument | `SystemRequests2` |
| `0x40` | monster | 1 | `MsSetMotion`, before the battle's own motion handler | the motion | `Motions` |
| `0x04` | monster | 1 | `MsSetMotion`, after the battle's own motion handler | the motion | `DefaultMotions` |
| `0x41`, `0x44`, `0x47`, `0x4a` | battle | 7-10 | `MsAtelRequestMagicCamera` (`0x00798000`) for command IDs `0x3xxx`, `0x2xxx`, `0x4xxx`, `0x6xxx` | the ID's low 12 bits | `CommandCameras`, `ItemCameras`, `MonsterCommandCameras`, `BossCommandCameras` |
| `0x4d`, `0x4e` | battle | 3 | `MsSetVoice` for a party member, a monster | the line | `PartyVoices`, `MonsterVoices` |
| `0x4f` + i | battle | 3 | `MsSetVoice` for speaker i | the line | `TidusVoices`, … |
| `0x6c`, `0x89` | battle | 6 | `MsAtelRequestSystemCheckExe` (`0x007982a0`), first and second | the event (below) | `BattleEvents`, `BattleEvents2` |
| `0x6d` + i | battle | 5 | `MsSetVoiceCheck` (`0x007a07b0`) for actor i | the cue: an action up to 8, else other moments | `TidusVoiceCues`, … |

`MsAtelChrInit` (`0x007976a0`) confirms the per-actor ranges: it gives the workers of signals
`0x29`-`0x37` (battle file), `0x05`-`0x20`, `0x4f`-`0x6a`, `0x6d`-`0x88` (`system_01`) their actor's
index, and those of a monster's `0x3d`, `0x04` and `0x40` the monster's. Signals `0x21`-`0x23`
(actors `0x1c`-`0x1e`, which have no `Target` name), the battle's `0x04` (`MsSetMotion` for a monster
with `+0x39` set), `0x42` and `0x6b` keep the workers' numbers.

## Actions

`MsActionRequest(i, who, k, special, mode, out)` runs actor i's action handler for `k`; a command
the script leaves (`Btl.UseCommand`) goes to the command cue (`mode` 0) or is inserted after `who`'s
(`mode` 1: counters). The call sites in FFX.exe:

| `k` | Call site | Function | When | Entry name |
|---|---|---|---|---|
| 0 | `0x00792566` | `MsCommandCueProcess` | the actor's turn, its cue entry without a command: the script picks it | `OnTurn` |
| 1 | `0x00792ba8` | `MsBattleProcess_InputCue_00792a90` | its turn comes up (once, actor `+0xdf7`) | `OnTurnStart` |
| 2 | `0x0079261e` → `0x007ad10c` | `MsCommandCueProcess` → `MsActionTarget` | each target of a command, before its damage (`who` the attacker, `mode` 1) | `OnTargeted` |
| 3 | `0x0078f5e0`, `0x0079257b` | `AfterDamageProcess`, `MsCommandCueProcess` | an actor a command hurt (`who` the attacker, `mode` 1), and a counter entry's turn | `OnHit` |
| 4 | `0x0078c92d` | `MsDamageCheckDeath` | it dies (`who` the killer) | `OnDeath` |
| 5 | `0x00783a6a`, `0x00783c32` | `MsBtlReadMonsterProcess`, `MsBtlReadPlayerProcess` | it comes into the battle | `OnEnter` |
| 6 | `0x007b2274` | `MsDeleteInputCue` | its input entry goes and its next turn is counted | `OnTurnEnd` |
| 7 | `0x00787d86`, `0x0078853d`, `0x007aec50` | `MsEffectBackToIdle_00787d70`, `MsEffectStart`, `MsParseCommand` | its command has played (back to idle) | `OnActionEnd` |
| 8 | `0x007afb19` | `MsPoisonProcess` | poison damage after its turn (`who` itself) | `OnPoison` |
| 9-0x14 | `0x007b2a30`, `0x007b2a8b`, `0x00792bc8`, `0x007b2ae8` | `MsCheckSummon`, `MsBattleProcess_InputCue_00792a90`, `MsDeathYoujinbou` | aeons only: Yojimbo's and the Magus sisters' commands, an aeon's turn | none |

`MsActionRequest` also sends `k` up to 8 as a voice cue (`MsSetVoiceCheck`), so the voice cue
workers' entries for those take the same names. Of the 332 monsters with an action handler, 330
handle `OnTurn`, 292 `OnHit`, 291 `OnDeath`, 276 `OnTargeted`, 258 `OnTurnStart`; `OnEnter` 29,
`OnTurnEnd` 18, `OnPoison` 5, `OnActionEnd` 3. Larva (`m001`): `OnTurn` picks the target and
`Btl.UseCommand`s, `OnHit` reacts to `DamageHp` and the attacker (`Target.Reaction`).

## Battle events

`MsAtelRequestSystemCheckExe(k)` runs `0x6c`, then `0x89`, of the battle file:

| `k` | Asked by | Entry name |
|---|---|---|
| 5 | `MsBtlReadManage` (`0x00783020`), the battle's start | `OnBattleStart` |
| 4 | `MsBattleProcess_Status_007917d0`, step `0x17`: the end status is known | `OnBattleEnd` |
| 1 | the same, step `0x1d`, end status 1 (`MsGetBattleEndStatus`: no party member still fights) | `OnPartyDefeated` |
| 0, 2, 3 | step `0x1d` for end statuses 2, 3 or 5, and 4 | none yet |

## Other entries

- Cameras: `OnCamera24` for camera 24 (decimal, as the scripts' calls write it).
- Motions: `OnMotion39`. The motion numbers `MsSetMotion` takes are the battle's motion requests
  (0x27 entering, 0x1c targeted, 0x12 escaping), not `BattleMotion` values.
- System requests: `OnRequest0` for `Btl.AtelRequestSystem_7097(0)`.
- Voices: `OnLine3` for line 3.
- Command cameras: the command, `OnDelayAttack` for `Command.DelayAttack` (`0x3006`), `OnPotion`.

A name the worker already has (the overlay's, an inline method's, a reserved one) is not given.
Renaming entries changes a worker's text, so a library class whose users now name entry 2
differently splits: `IdleWorker2WithEntry2` (`OnTurnStart`, 23 users), `_2` (`OnTurn`, 20) and `_3`
(`OnBattleEnd`, 3). Library classes 432 → 435, workers deriving from one 3,002 → 3,000.
