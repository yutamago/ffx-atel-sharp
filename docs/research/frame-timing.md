# Frame timing: what a script frame is, and what a frame-rate mod has to do

How often FFX.exe (Steam HD Remaster) runs its scripts, what else counts in frames, and what that means
for `Wait.Seconds`, an uncapped or monitor-rate frame rate, fast-forward, and waits shorter than a
frame. Static research only: FFX.exe's code (objdump, ffx-rust's `tools/refs.py` cross-references and
`symbols/ffx.sym`) and ffx-rust's ports of the functions involved (`crates/ffx-sg/src/m5main_loop.rs`,
`crates/ffx-sg/src/state.rs` `frame_sync`, `crates/ffx-script/src/vm/bridge/run.rs`). The game was
not run for this. The engine findings are also in ffx-rust (`docs/formats/frame-timing.md`). The mod
built on this research is described at the end ("What the Frames mod does").

## Answers

1. **`Wait.Seconds(1.23f)`: yes, added.** The game runs the scripts of the field, events and battle
   30 times a second, the same rate in all three (below). So `Wait.Seconds(s)` compiles to
   `Wait.Frames(n)` with n = s × 30 rounded to the nearest frame, halves away from zero:
   `Wait.Seconds(1.23f)` is `Wait.Frames(37)` (36.9 frames). The argument has to be a constant,
   because the compiler counts the frames ([ATL3005](../analyzers.md#atl3005) otherwise). A wait of
   less than one frame is [ATL3006](../analyzers.md#atl3006). The decompiler still writes
   `Wait.Frames`: the bytes don't record whether the source said seconds. See
   [language.md](../language.md#waiting-in-seconds).
2. **An uncapped or monitor-rate mod doesn't need `Wait.Seconds`, and seconds wouldn't help it.**
   `Wait.Frames` is not the only thing that counts frames: motions, moves, turns, camera moves and
   eases, effects, fades and battle all step once per world tick. The scripts are one consumer of
   that tick among many. If the game draws at 60 fps or more, the logic speeds up only because the
   tick is tied to the drawn frame. Fix that, and every frame count in the game stays right, scripts
   included. Rewriting waits in seconds would leave everything else at the wrong speed. The mod has to
   keep the tick at 30 a second and draw more often than it ticks, interpolating in between
   ("(a) Decoupled drawing" below).
3. **×100 without drawing 3,000 frames a second is possible.** The game already runs several ticks
   per drawn frame: the PC booster (×2, ×4) does it, catch-up after a slow frame does it, and so
   does the PS2 debug fast-forward `Sg_SetExecFrames(n)` (up to 99 ticks per frame). The ticks still
   cost CPU, so ×100 means simulating 3,000 ticks a second. That is likely too much for one core,
   but it is unmeasured. Audio, movies, loading and voice-synced cut scenes don't speed up with the
   ticks ("(b) Fast-forward" below).
4. **No wait shorter than a frame.** A worker's script runs at most once per tick. Any native that
   polls, `WaitFrames(0)` included, ends the worker's run for that tick, and the frame count is an
   integer. A script cannot act between two ticks, because nothing in the game happens there for it
   to see. At 30 ticks a second, one frame (1/30 s) is the finest step.
5. **Unity's timing.** FFX.exe already uses a fixed step with an accumulator (`FixedUpdate`), but
   it always runs at least one tick per drawn frame, has no interpolation, and scales time only by
   1, 2 or 4. A `FixedUpdate`-like model (0 ticks per frame, any `timeScale` including slow motion,
   interpolated drawing) is a realistic Fahrenheit mod and keeps the simulation exact. Moving
   everything to `Update`/`Time.deltaTime`, or changing the tick rate (30 → 60 Hz), means rewriting
   every per-tick duration and rate in the code and in the scripts' data. That belongs in the Rust
   port, if anywhere ("Unity's timing model" below).

## The loop

| Step | Where | What |
|---|---|---|
| Frame limiter | `Game_Main` `0x0062ee50`, `Sleep` at `0x0062f3bb` | A frame (`App_Frame` `0x0062e340`) every `g_FrameVsyncs` (`0x00c30e88`) × 1/59.94 s of `Phyre_PTimerWin32_GetTime`; sleeps away the rest. No vsync wait of its own; this is the cap |
| Frame interval | `graphicSetFlipVsnc` `0x006430e0` → `PWindowWin32Base::SetFlipVSyncInterval` `0x00625090` (clamps to 1..2) | Called with 2 by `Sg_MainInit` (`0x00820865`), with 1 when the main menu opens and 2 when it closes (`TkMenuMainLoop` `0x008a9dac`, `0x008a9fe8`), 2 when the sphere grid opens and 1 when it closes (`eiAbmStart` `0x00a56002`, `eiAbmExit` `0x00a5473d`). These five are the only callers |
| Update | `FFXApplication::animate` `0x0042f520` → `updateFFX` `0x00822840` | `updateFFX` calls `Sg_MainLoop(dt)` (`0x00820ae0`) again while `g_VoiceHold` (`0x012fb808`) is not 0, each call taking one off |
| Speed-up booster | `SpdCtrl_ScaleElapsedTime` `0x006f7340` | `dt` × `g_SpdCtrlScales[level]` (`0x00c38e1c`: 1, 2, 4; F1 steps the level) where the booster is allowed; mutes the sound while on |
| Tick count | `Sg_FrameSync(dt)` `0x00821e80` | `dt` capped at 0.3 s (`0x00b3eb44`). Ticks due = (carry + `dt`) × 60. Fewer than 2 due, or `Sg_CanSpeedUp` (`0x0081fd30`) 0: `g_VoiceHold` = 1, carries cleared. Otherwise `g_VoiceHold` = whole part of (carry + `dt`) × 30, and the fraction carries over. A voice-synced cut scene (`g_SyncOn`) counts from the sync clock instead (below) |
| World tick | `Sg_MainLoop`'s update loop (port: `m5main_loop.rs` `update`) | `Sg_GetCurExecFrames()` (`0x0082d7e0`: `0x00c496d4`, 1 by default) times: `Sg_ControlPlayer`, `MsBattleProcess`, `TkUpdateEventJump` (the scripts, below), `TOControlMessageWindow`, effects, `MsCameraProcess`, field, `Ch_CalcMain` (characters, motions). Then once per `Sg_MainLoop`: `Ch_CalcMiddle`, the field animation, the draw |
| Draw | `Sg_MainLoop`'s draw, then Phyre's frame | Each `Sg_MainLoop`, repeats included, builds its draw. Phyre queues one flip per `App_Frame` (`PRenderer_flip` `0x005b6af0`), which the render thread (`0x00616010`) presents one update behind the main thread (ffx-rust `docs/harness.md`, "Frames and threads") |

At the 30 fps interval (2 vsyncs, `dt` = 1/29.97 s), each frame has (carry + `dt`) × 60 = 2.002 ticks
due, and the hold is ⌊1.001⌋ = 1. The 0.001 left over adds up to a second tick in about one frame in
1,000 (ffx-rust `docs/harness.md` sees this as `g_VoiceHold` 2), so the world runs at 30.0 ticks a
real second, a little more than the 29.97 frames drawn. With `Sg_CanSpeedUp` 0 the carries are
dropped and it is exactly one tick per frame: 29.97 a second. When a frame is late, the next one
catches up with more ticks, at most ⌊0.3 × 30⌋ = 9.

At the 60 fps interval (main menu), only 1 tick is due per frame, so the hold is 1 and there is one
`Sg_MainLoop` per frame: 60 a second. But the main menu stops the world update: `Sg_MainLoop` takes
the menu path (`Sg_MainCheckMenu` `0x00820750`) and goes straight to the frame's end. Scripts, motions and the field
do not tick behind the menu. The same holds for the pause, the save screen and scene loads (`stop`).
So **scripts tick only in the 30 fps modes**, at 30 a second.

## What a script frame is

`TkUpdateEventJump` (`0x0088d3d0`) runs once per world tick ("every frame in `Sg_MainLoop`'s loop",
ffx.sym). Through `Atel_EventFrame` and the ctrl work's callback `AtelExecCtrlEvent` (`0x00867740`),
it runs three passes of `Atel_RunCtrlWorkers` (`0x00867950`). The first pass runs `Script_Run`
(`0x008641e0`) on every worker. The other two run only workers with a level that a request just
started (`+0x36` bits `0x180`), so a new request begins in the same tick. Every caller of `Script_Run`
is in these passes, or is the debugger (`AtelDecodeCommand`, `Atel_CtrlStart`,
`AtelExecCtrlEventInit`).

A native that polls ends the worker's run for that tick: opcode `35`/`58` (`run.rs`) starts the
native and polls it once, and a poll without the "run on" bit (4) leaves the run with an empty
stack. `Native_WaitFrames_0000` (`0x0085c4e0`) stores n, and its poll (`0x0085c6b0`) subtracts 1
and is done at 0 or below. So `WaitFrames(n)` started in tick t ends in tick t + n − 1, and the
next statement runs in tick t + n. `WaitFrames(1)` and `WaitFrames(0)` both resume in the next tick.

Two exceptions:

- **Movies with a camera.** `Native_b00a_MovieStartPicture` sets `g_WaitFramesByTime`
  (`0x0112a008`), and `destroyMoviePicture` clears it. While it is set and `movie_have_camera`
  (`0x0076efc0`: movies `0x20`, `0x2e`, `0x46`, `0x48`) holds, the poll subtracts
  `graphicVideoGetElapsedFrame()` (`0x00645d90`: the movie's frames since the last update) instead
  of 1. The wait then counts the movie's own frames, 29.97 a second (ffx-rust
  `docs/formats/platform.md`, "Movies"). `Atel_MoveUpdate` (`0x00868990`) does the same for moves.
- **The timing patch.** `WaitFrames(10)` in battle scene `0x610000` while magic `0x290` is loaded
  waits 40 frames (the start slot's one-off patch, `data/natives.json`). `Wait.Seconds(1/3f)` is
  `WaitFrames(10)` and is patched the same way.

`Script_Run` also has a per-worker speed: `+0x94` × `g_GameSpeed` (`0x01326bd8`), added to
`+0x98` (`0x0086426b`..`0x008642cc`). Below 1 the worker's run stops at once. At 1 or more it runs
normally. Nothing makes it run more than once per tick. `g_GameSpeed` is 1.0 (`AtelInitTotal`), and
its only setter (`0x00871020`) has no callers. This is not a rate control.

## Frames and real time

| Counts world ticks (speeds up with them) | Counts real time or its own clock |
|---|---|
| `WaitFrames`, the poll natives (motion, move, turn, camera, message waits) | Movies: `updateTexture` takes one decoded frame per update (or per render command for `0x20`, `0x2e`, `0x48` and loops). The sound is an FMOD stream in real time, with no A/V clock (ffx-rust `docs/formats/platform.md`) |
| Motions (`SG_UpdateMotion`, in `Ch_CalcMain`), moves and turns (`Atel_MoveUpdate`), `Sg_AnimValF` eases | FMOD: music, voices and sounds mix on FMOD's threads in real time. A sound starts in a tick but plays at its own rate |
| The camera (`MsCameraProcess`), the effects' update half (`MsEffectProcess(0)`), the field (`Yn_FieldProc_0091a8c0`) | Voice-synced cut scenes: with `g_SyncOn` (`0x012fb8a0`, set by `Sg_SyncCutScene` `0x00821d60` from `iSyncGetData`, the remaster's `SyncData` tables), `Sg_FrameSync` steps ticks while the sync clock (`graphicGetTimeInMicroseconds`) is behind and spins while it is 0.1 s or more ahead, so the scene keeps pace with the voices in real time |
| Battle (`MsBattleProcess`): turn order, counters, the battle's waits | Loading: the asset loader thread reads in real time, and the world update stops (`Sg_IsSceneLoading` `0x0081fcd0`) until it is done |
| Message windows (`TOControlMessageWindow`), the pad's press edges as scripts see them | Input: the pad word is built once per drawn frame (`Virtuos_InputManager_updateInput` `0x00632a30`); hold repeats after 0.2333 s, then every 0.1 s |
| Once per `Sg_MainLoop`, not per tick: `Ch_CalcMiddle`, the field animation (`Yi`), the v-blank counters (+2), `Sg_MainCalcRate`'s rate | The Iggy (Flash) PC menus at their own 30 fps; the speed-up booster's icon |

`Sg_SetKeepFps` (native `0x4022`, set by battle magic) has motions step by `Sg_MainCalcRate`'s rate
(`0x023cbbec`, 0x100 = one tick). That is the v-blank count since the last call, which is 2 per
`Sg_MainLoop`, or the sync data's frame in a synced scene. It is the PS2's frame-drop compensation
and stays in ticks.

## (a) Decoupled drawing at the monitor's rate

Raising the rate the easy way changes `g_FrameVsyncs`, or replaces the limiter's interval with the
monitor's. Each frame then runs `Sg_MainLoop` with one tick (fewer than 2 due), so the game runs
twice as fast at 60 fps and faster still above. The tick has to be separated from the drawn frame.
A Fahrenheit mod could do it like this:

1. **Limiter.** Hook `Game_Main`'s `Sleep` (`0x0062f3bb`) or `Phyre_PTimerWin32_GetTime`. Run
   `App_Frame` at the monitor's rate (from the swap chain's refresh rate, or with no cap), and
   present with vsync or without.
2. **Tick accumulator.** Keep the game's own `dt` (the frame's real time) and add it to a 30 Hz
   accumulator. When a tick (or several) is due, let `Sg_MainLoop` run as now, passing it the
   accumulated time as `dt`, so `Sg_FrameSync` sees the 30 fps it expects. When none is due, draw
   without updating.
3. **Drawing without a tick.** `Sg_MainLoop` both updates and builds the draw, so a frame without a
   tick needs a path that only draws. `Sg_SetExecFrames(0)` (the PS2 debug pause) is the nearest:
   the update loop runs no tick. It still runs `MsEffectProcess(0)` and the camera, and once per
   call `Ch_CalcMiddle`, the field animation, the effects' draw half (`op_loop_ffx_after`,
   `MsEffectProcess(1)`), `Sg_FrameSync`'s pad scan and v-blank count, `graphicVideoUpdate`, the
   fades and the menu update. Each of these would have to be held on frames without a tick (hooks
   that return early, or their state saved and restored), or they run at the drawn rate.
4. **Interpolation.** Save, for each tick, the state the draw reads: the camera and screen
   matrices (`MsScreenProcess`, `MsCopyCameraMatrix` `0x007c0070`, `MsCopyScreenMatrix` `0x007bba50`), each character's
   root and bone matrices (`Ch_CalcMiddle`'s output), and the field's moving parts. Before drawing a
   frame between ticks, write the lerp (slerp for rotations) of the last two ticks at the
   accumulator's fraction, and put the latest values back after. That shows the world one tick late,
   about 33 ms. Effects and particles (the VU-emulated `ppp` programs), message windows and the PS2
   2D layers have no clean state to interpolate. They would stay at 30 Hz unless each is handled.
5. **Render thread.** Phyre already draws update n while the main thread computes n + 1. A drawn
   frame without a tick is another `App_Frame` with a flip, so the handshake stays as it is (one
   flip per `App_Frame`). The cloth's staging (ffx-rust `docs/host.md`, "The cloth race") expects
   one stage per submit and needs checking.

Feasibility: steps 1-3 are a few hooks. Camera interpolation is moderate work and covers most of the
perceived smoothness on the field. Full interpolation of characters, effects and UI touches many
systems and is a large project. The 30 Hz simulation itself stays exact, and with it every frame
count, scripts included.

## (b) Fast-forward: N ticks per drawn frame

The game has three ways to run several ticks per drawn frame:

| Way | How | Limit | Draws per tick |
|---|---|---|---|
| Booster | `g_SpdCtrlScales` (`0x00c38e1c`) × `dt` → `Sg_FrameSync` → `g_VoiceHold` → `updateFFX` repeats `Sg_MainLoop` | `dt` capped at 0.3 s: at most 9 ticks per frame, about ×9 at 30 fps. Only where `SpdCtrl_ScaleElapsedTime` and `Sg_CanSpeedUp` allow (not in movies, synced scenes, the sphere grid, the excluded maps and spells) | Each repeat builds a full draw (the harness sees a double tick draw the effects twice) |
| Exec frames | `Sg_SetExecFrames(n)` (`0x0082da60`, `0x00c496d4`, clamped to 0..99): the update loop runs n ticks per `Sg_MainLoop` | 99 per `Sg_MainLoop` | One draw; the effects' draw half runs n times |
| Both | exec frames × booster hold | 99 × 9 | |

A mod for ×100 would set exec frames (for example 50) and the booster scale (×2, with the 0.3 s cap
in mind), or call `Sg_MainLoop`'s update part in a loop of its own. Some of it does not follow:

- **What runs once per `Sg_MainLoop`.** With exec frames, `Ch_CalcMiddle`, the field animation, the
  fades, `Sg_FrameSync` (pad scan, v-blank count), `graphicVideoUpdate` and the menus run once per
  drawn frame, while the world runs n ticks. Field animations and movies stay at ×1.
  `Sg_MainCalcRate` forces the motion rate to 0x100 when exec frames is not 1. The booster repeats
  all of `Sg_MainLoop`, so it has none of these gaps, but it builds a draw per tick.
- **Audio.** FMOD plays in real time. Sounds triggered per tick pile up ×N, and music and voices
  don't speed up. The booster mutes the sound (`Sg_SetMuteSound`) and mutes the battle subtitles.
- **Movies.** The video advances one frame per update and the sound plays in real time, so a faster
  update desyncs them. `Sg_CanSpeedUp` refuses a video without a movie camera, and the booster stays
  off while `g_WaitFramesByTime` is set.
- **Voice-synced cut scenes.** `g_SyncOn` paces the ticks to the sync clock (real time), so the
  booster does nothing there. Exec frames would run n ticks per synced step and leave the voices
  behind.
- **Input.** The pad word is read once per drawn frame, and the press edges once per `Sg_MainLoop`
  (`Sg_ControlMain`'s `0x023c44a8`). With exec frames, all n ticks see the same buttons. Whether a
  press then counts once or n times in each reader (menus, scripts' `Pad` natives) is not checked.
- **Loading.** Real time; the world update waits for it.
- **Render thread.** One flip per `App_Frame` either way. Exec frames draws once, so the GPU sees no
  extra work. The booster's repeats build extra draws, and the main thread does all their work.
- **CPU.** Every tick is simulated. The hook harness's `--fast` mode ran 461 whole frames (update,
  draw, present) a second on its test machine (ffx-rust `docs/harness.md`), about ×15 in full
  frames. A tick without its draw is cheaper, but this has not been measured. ×100 would be 3,000
  ticks a second, which is likely beyond one core in heavy scenes.

## Unity's timing model

How FFX.exe's timing compares with Unity's two models, and what moving it to either would take.

### What FFX.exe has today

FFX.exe is already a fixed-step simulation with an accumulator, close to Unity's `FixedUpdate`, with
four differences:

| Unity | FFX.exe |
|---|---|
| `Time.fixedDeltaTime` (0.02 s by default) | One tick, 1/30 s. The 30 and 60 are `.rdata` constants of `Sg_FrameSync` (`0x00b922c8` 30.0, `0x00b4a180` 60.0), and every system counts whole ticks (below) |
| Accumulator: 0, 1 or more `FixedUpdate`s per rendered frame | `Sg_FrameSync`'s carries (`0x012fb7f8`, `0x012fb7fc`) give 1 to 9 ticks per drawn frame, **never 0**: with fewer than 2 due, the hold is 1 and the carries are cleared. A frame faster than 1/30 s still ticks once. That is why drawing at 60 fps runs the game at ×2 |
| `Time.maximumDeltaTime` (0.333 s) | `dt` capped at 0.3 s (`0x00b3eb44`): at most 9 ticks per frame |
| `Time.timeScale`, any float | The booster's `g_SpdCtrlScales` (`0x00c38e1c`: 1, 2, 4) scales `dt` before `Sg_FrameSync`. A scale below 1 does nothing, because a frame never gets fewer than 1 tick |
| Rigidbody interpolation between the last two fixed steps | None. Each drawn frame shows the last tick |
| `Update` (once per rendered frame, `Time.deltaTime`) | Not separate. What runs once per `Sg_MainLoop` (`Ch_CalcMiddle`, the field animation, fades, the draw) is tied to the tick at 30 fps too |

### (1) Fixed step like `FixedUpdate`: realistic as a mod

This is (a) and (b) above, plus 0-tick frames and a fractional time scale:

1. **0 ticks per frame.** Hook `Sg_FrameSync` (`0x00821e80`, ported in `ffx-sg` `state.rs`
   `frame_sync`) so that ticks = ⌊(carry + `dt` × scale) × 30⌋, 0 included, with the rest carried.
   The game's own 60 Hz first test ("fewer than 2 due → 1") goes. A 0-tick frame takes the draw-only
   path of (a): `Sg_SetExecFrames(0)` plus holding what runs once per `Sg_MainLoop`.
2. **`timeScale`.** Hook `SpdCtrl_ScaleElapsedTime` (`0x006f7340`) to multiply by any float: 0.25 for
   slow motion (a tick every fourth frame at 30 fps, every eighth at 60), 100 for fast-forward. Above
   ×9, raise the 0.3 s cap or use exec frames ((b)). Unlike the booster, this works everywhere,
   including the places `SpdCtrl_IsMapExcluded` and `Sg_CanSpeedUp` exclude, so those cases (movies,
   voice-synced scenes) need a decision of their own: keep them at ×1, as the booster does.
3. **Interpolation.** As in (a) step 4: camera first, then the characters' matrices. In slow motion
   it matters more, because at 0.25 a frozen tick lasts 4 drawn frames.
4. **Real-time parts stay real time.** FMOD, movies, the sync clock and loading (the table above) are
   not ticks. Slow motion leaves the music and voices at ×1, as Unity's `timeScale` does with an
   `AudioSource` unless its pitch is changed. Scaling FMOD's pitch to match would put voice-synced scenes out of step.

The simulation stays bit-exact: the same ticks run in the same order, only spread differently over
drawn frames. Determinism, replays and every frame count in the scripts stay valid.

### (2) Variable step like `Update` with `Time.deltaTime`: belongs in the Rust port

Every system that steps once per tick would take the frame's `dt` instead. These count whole ticks
today:

| System | Where | What counts ticks |
|---|---|---|
| Script VM | `Script_Run` `0x008641e0`, through `TkUpdateEventJump` `0x0088d3d0` | One run per worker per tick. Waits are integer tick counts: `Native_WaitFrames_Poll_0000` `0x0085c6b0`, 124,718 `WaitFrames` calls in the shipped scripts |
| Natives with durations | `TurnSetFrames`, `MoveSetFrames`, `GetTurnFrames`, `SetMapFadeFrames`, the camera's eased moves (`MoveEased`, `FocusTimeEase`), message waits | Arguments in frames, compiled into the scripts as constants |
| Natives with rates | `Native_SetTurnSpeed` `0x0085a480` (radians a frame, default π/64), `MoveSpeed`, motion speeds | Per-frame steps, compiled into the scripts as constants |
| Moves and turns | `Atel_MoveUpdate_00868990` | One step of the move record per tick |
| Motion | `SG_UpdateMotion` `0x00838c90`, `SG_SeqFrame` `0x008377c0`, `Ch_MotionLoopFrame` `0x008392d0` | Time `+0x740` += speed per tick in 8.8 fixed point, so half steps exist (`g_ChrMotionHalf` `0x013017f8`, set only from a debug menu, `ChrMenu_buttons_0084cf60`; `g_FrameRate88` `0x023cbbec` with keep-fps). Sequences step in whole frames |
| Eases | `Sg_AnimValF` `0x00836020` | value += (target − value) / frames, frames −1 per tick: a per-tick ratio, not a rate |
| Collision and ground | `Atel_MoveUpdate` against the id (collision) mesh (`Sg_IdGetNormal` `0x0083e130`, id data `0x01301a84`), the player's control (`Sg_ControlPlayer` `0x0082d180`) | A move per tick, tested once per tick. A longer step could pass through thin walls |
| Cloth | `Sg_ClothStep` `0x00822de0` | One Verlet-style step with rods and collision per tick; the CRT `rand()` for wind |
| Particles and effects | The `ppp` programs (`pppDrawShapeField*Emit`, `0x00742750`..`0x00745f70`), `MsEffectProcess` `0x00787e00`, `op_loop_ffx_after` `0x007ff680` (`g_OpLoopCount` `0x023328d4`) | Integer countdowns and per-tick velocities, recompiled from PS2 EE and VU code |
| Camera | `MsCameraProcess` `0x007be110` | Channels step per tick; movie cameras use the video frame (`MovieCameraFrame_0076edc0`) |
| Battle | `MsBattleProcess` `0x00790c10` and its steps (`_Order`, `_ChrDamage`, `_Status`, `_InputCue`) | Turn order is by counter (CTB), not time, but every animation, effect and wait inside a turn counts ticks |
| Field and fades | `Yn_FieldProc_0091a8c0`, `Sg_DrawFilter` `0x0082c0c0` (i16 counters), `Pause_Update` `0x008ab340` | Per-tick counters |
| Encounters | `MsBattleEncountExe` `0x00780d10`, `ResetEncountExe` `0x00780ff0` | Rolled against step counters that the player's moves advance in the world update |
| Sound sync | `Sg_SyncCutScene` `0x00821d60` | The `SyncData` tables give a frame per step (`frame << 7` into `0x012fb828`) |
| Randomness | `g_Vu0_R` `0x00c0a000` (`op_random` `0x007e6700`, effects), the main thread's CRT `rand()` (drawn by `op_loop_ffx_after` each tick), the cloth's per-job seeds; the battle's generators (`0x00c42200`, `0x01135ee0`) | Drawn a fixed number of times per tick. With a variable step, the draw count follows the frame rate, so the same inputs give different results. The harness's replays depend on the fixed tick (ffx-rust `docs/harness.md`, "Pinned state") |

Converting these is not a mod. The durations and rates are data in 1,621 compiled scripts, the motion
and effect files and the battle tables, all in ticks. The code is PS2-era C and recompiled EE and VU
code, in thousands of functions. Ease, collision and cloth would also change behaviour (`Sg_AnimValF`'s
1/frames ratio, tunnelling, cloth stability) unless each gets a careful dt form. In the Rust port it
is possible system by system, but it gives up the port's parity with the original (ffx-rust `docs/rewrite-plan.md`,
"Determinism"). The usual compromise is model (1) with interpolation: Unity games keep physics in
`FixedUpdate` for the same reason.

### Changing the tick rate (`Time.fixedDeltaTime`), e.g. 30 → 60 Hz

Patching `Sg_FrameSync`'s constants (30 → 60, 60 → 120) and the limiter to 1 vsync gives 60 ticks a
second, and everything above runs twice as fast. Every per-tick quantity would have to be halved:

- **Scripts.** Every frame count (`WaitFrames(n)` → 2n, the turn, move, fade and camera frames) and
  every per-frame rate (turn speeds, move speeds) in all 1,621 scripts. The linker could rewrite
  constants, but computed values (`WaitFrames(V3)`, `GetTurnFrames`'s result) and frame comparisons
  (`if (count > 30)`) cannot be found mechanically.
- **Motion.** The 8.8 step could be halved (the debug `g_ChrMotionHalf` does it), but the sequences'
  whole-frame waits cannot.
- **Effects.** Integer countdowns and velocities in the `ppp` and `opus` programs, which are data.
- **Eases.** `Sg_AnimValF` over 2n frames gives a different curve unless the ratio changes too.
- **Sync data and movie cameras.** Keyed on 30 Hz frames and 29.97 Hz video frames.
- **Randomness.** Twice the per-tick draws, so a different sequence.

On FFX.exe that is not realistic: half the work would be data that cannot be told apart from other
numbers. A port could do it with each system taking its own step, which is model (2) with a fixed
step. Smooth 60+ fps is cheaper through interpolation (1), which keeps the 30 Hz simulation.

## (c) Waits shorter than a frame

None. A worker runs once per tick, and the next tick is the next time anything in the world
changes. `Wait.Seconds` therefore rounds to whole frames, and less than one is an error. At a
decoupled draw rate (a), a script still sees only ticks. A mod that wanted finer steps would have
to raise the tick rate itself, and that changes the meaning of every frame count in the game's
scripts and code (all of them written for 30), so it is not a mod-sized change.

## What the Frames mod does

`src/Ffx.Frames` (Fahrenheit mod `yuta.frames`, [README](../../src/Ffx.Frames/README.md)) is model
(1) above: a fixed 30 Hz tick with an accumulator that can run 0 ticks a frame, any time scale,
and the camera and the characters interpolated. It is static work: everything below comes from
FFX.exe's code and ffx-rust's ports. Its first in-game run (2026-11-07) crashed on the first frame;
"Hooking: why the first run crashed" below has the cause. The second (2026-11-07, Unlimited, about
200 fps) kept the game's speed, but the effects (the pyreflies of the opening scene) ran about
6.7 times too fast and slow character motions looked choppy: "Effects between ticks" and
"Characters between ticks" below are the answer, not yet tested in the game. The open questions
are at the end of this section.

### The frame, hook by hook

| Where | Hook | What the mod does |
|---|---|---|
| `Game_Main` limiter (`0x0062ee50`) | Code patch: the operand of `fild dword [g_FrameVsyncs]` at `0x0062f2fc` and of `fmul qword [0x00b412d8]` (1/59.94) at `0x0062f318` | Both point at the mod's own int and double: 1 and the target interval (1/refresh, 1/fps, or 0 for no wait). With the mod off they hold `g_FrameVsyncs` and 1/59.94 again. The bytes are checked first (`db 05`, `dc 0d` and the original operands) |
| `updateFFX` (`0x00822840`), once per drawn frame | Call-site hook: its one caller, `call` at `0x0042f5b6` | Reads the game, asks the accumulator for the frame's ticks (`FrameDirector`), sets the limiter, mute and vsync. A frame without a tick either runs the update as a **Redraw** or, as a **Repeat**, returns without calling the original (what `FFXApplication::animate` does while the Esc menu is up) |
| `Sg_MainLoop` (`0x00820ae0`), once per game update | Hook, chained after Fahrenheit's own | Puts the true camera and screen matrices back before the update. On a Redraw it restores the Eternal Calm copyright counter (`0x012fb7c0`) after |
| `Sg_FrameSync` (`0x00821e80`) | Hook | **Tick frame**, first update: the original runs (so `Sg_SyncCutScene` and the sync clock work as before), then, unless `g_SyncOn`, its carries are cleared, `g_VoiceHold` = the plan's updates and the exec frames (`0x00c496d4`) = this update's ticks. Later updates set their own exec frames. In a voice-synced scene the game's sync-paced hold stays. **Redraw**: the original does not run at all, so the pad scan (`TkScanControler`), the v-blank counters (+2), the sync clock and the effect read poll stay put; exec frames = 0 |
| `TkUpdateScanControler` (`0x00889a80`), `Sg_ControlMain` (`0x0082d070`) | Hooks (the first at its two calls, `0x00820b84` in `Sg_MainLoop` and `0x0089ae60` in `TOControlBtlWindow`), return at once on a Redraw | The pad's port scan and the controls (press edges) run only in updates that tick, so a press is never used up by a frame without a tick |
| `MsEffectProcess` (`0x00787e00`), `op_loop_ffx_after` (`0x007ff680`) | Hooks | The effects: held on a Redraw, their draws replayed ("Effects between ticks") |
| `op_gs1_transfer` (`0x007eb580`), `yiCallFieldParticle` (`0x009083e0`), `pppPartLoop` (`0x00762240`), `op_ot_draw` (`0x007ec470`) | Call-site hooks: their one call in `Sg_MainLoop`'s effect loop (`0x00821477`, `0x0082148c`, `0x008214a2`, `0x008214ba`); their other callers keep the function | The same |
| `VfxGeometry_Register` (`0x006a52f0`, `VFXDynamicGeometry` vtable `+8`, thiscall), `graphicVFXSyncPath` (`0x00645840`), `PhyreScene_addVFXDrawEvent` (`0x00656940`, thiscall) | Hooks | Record what the effect calls above hand the renderer on a tick (game thread only) |
| `JobSchedule_Ch_Cloth_PerCharacter` (`0x00a44b40`), `Ch_DrawClothClear` (`0x00830b90`) | Hooks (switch, on) | Held on a Redraw: `Ch_DrawMain` steps the cloth once per game update |
| `Ch_DrawMain` (`0x0082ed50`), `graphicCharacterInstanceSetWorldMatrix` (`0x0063c3e0`), `…ByMdlID` (`0x0063c430`) | Hooks | Draw the characters between ticks ("Characters between ticks") |
| `MsCameraProcess` (`0x007be110`), `Sg_CameraSetPlayer` (`0x0083f2e0`) | Hooks | Held on a Redraw: with exec frames 0, `Sg_MainLoop` still runs both when the camera is idle (`0x012fbbb0`) |
| `Ch_CalcMiddle` (`0x00833390`) | Hook (switch, on) | Held on a Redraw. It is not idempotent: besides the bones it fires `Ch_MotEvent` (motion events). After a tick, where the characters are recorded |
| `yiAnimMain` (`0x00908320`) | Hook (switch, on) | The field's animation, held on a Redraw |
| `graphicVideoUpdate` (`0x00645df0`) | Hook | Held on a Redraw. During a movie the mod repeats instead of redrawing anyway: the picture steps per update, and for some movies per draw |
| `Sg_DrawFilter` (`0x0082c0c0`) | Hook (switch, on) | On a Redraw the fade state (`0x01300700`..`0x0130075f`: a flag word and the 5 slots at `0x01300708`) is saved, the fades drawn, and the state put back, so the counters step once per tick |
| `Sg_MainCalcRate` (`0x008206c0`) | Hook (counts) | `Sg_MainLoop` calls it only on the way to the world update. A frame whose updates never called it (menu, pause, save screen, load) makes the next frames without a tick Repeats: their logic runs once per update, and a second update would speed it up |
| `TkUpdateEventJump` (`0x0088d3d0`) | Hook (counts) | The world ticks that ran, for the window's "ticks/s" |
| `graphicCameraSetWorldMatrix` (`0x00639860`) | Hook | See "The camera" below |
| `Renderer_PresentFlip` (`0x00593760`, thiscall, render thread) | Hook | Counts presents (the drawn fps). For vsync on or off it sets the renderer's byte `+0x131c` around the call (the present's sync interval is 1 when it is not 0) and puts it back |

The main menu runs at 60 fps in the game (`g_FrameVsyncs` 1), one update per frame. There the
accumulator ticks at 60 a second (60 / `g_FrameVsyncs`), so the menus keep their speed at any
frame rate, and frames between those updates are Repeats.

The time scale multiplies the frame's time before the accumulator, and so does the booster's
factor (`g_SpdCtrlScales[level]` while `g_SpdCtrl_b8` says it scaled). It stays ×1 where
`g_SyncOn`, `g_WaitFramesByTime`, `g_MovieFading` or `Sg_CanSpeedUp() == 0`. With "keep the
booster's limits" it also stays ×1 where `SpdCtrl_ScaleElapsedTime` refuses: `SpdCtrl_IsMapExcluded(g_CurrentMapNo)`,
`SpdCtrl_IsMagicExcluded` in battle, `graphicGetAbmap()` (sphere grid), `g_SaveMachineBusy`. Up to 4 ticks a frame
(configurable) are whole updates, as the booster runs them. Past that, the ticks are spread over
4 updates through exec frames (up to 99 each), so ×100 at 144 fps is about 21 ticks per frame in
4 updates. Above ×1 the mod calls `Sg_SetMuteSound(1)` (and 0 again unless the booster is on).

### Findings used

- **The limiter** starts a frame when `over` = now − last − interval ≥ 0, sets last = now −
  min(over, interval) (catch-up by at most one interval), and otherwise sleeps
  `(int)(over × −1000)` ms (`0x00b41328` is −1000.0, truncated). A remaining time under 1 ms
  sleeps 0 and loops.
- **`Sg_GetCurExecFrames`** (`0x0082d7e0`) returns (byte `0x013007b1` ? byte `0x013007b2` : byte
  `0x00c496d4`) | byte `0x013007b3`, all signed bytes. The mod writes `0x00c496d4` and puts the
  value back at the end of the frame. `Sg_MainCalcRate` forces the motion rate to 0x100 when it is
  not 1.
- **The camera.** `MsScreenProcess` (`0x007bc110`), which the draw calls once per screen, computes
  `g_ScreenMatrix` (`MsScreenMatrix`) and `g_CameraMatrix` (`MsCameraMatrix`) and ends with
  `Cam_Apply`, which hands `g_CameraMatrix` to the renderer through `graphicCameraSetWorldMatrix`.
  So the renderer's view is set during each update's draw, not in the tick. A point is drawn at
  `p × camera × screen` (`l6_map.rs`): the screen matrix is view to screen, separate from the
  camera. `MsCopyCameraMatrix`/`MsCopyScreenMatrix` copy the two globals and have 13 and 10 callers
  (effects, the sound's camera position, `UpdatePhyreViewProjectionMatrix`).
- **The pad.** `TkUpdateScanControler` (each `Sg_MainLoop`, before `Sg_FrameSync`) scans the ports.
  `TkScanControler` (in `Sg_FrameSync`) runs `Pad_UpdateFrame`, and `Sg_ControlMain` reads the
  history.

### Hooking: why the first run crashed

Fahrenheit (alpha12) hooks with MinHook. `MH_CreateHook` overwrites a function's first 5 bytes with
`jmp detour`, copies the whole instructions it overwrote into a trampoline, and ends the trampoline
with a `jmp` back to the first byte after them. Fahrenheit chains several hooks on one function by
hooking the previous trampoline, and a hook's `chain_from(hook).fnptr` is that trampoline. MinHook
does not check whether the function itself branches into bytes 1 to 4. Such a branch lands inside
the `jmp`'s address and runs it as code.

Two of the functions the mod hooked do branch there, both with a loop whose head is the third byte:

- `TkUpdateScanControler`: `push esi; xor esi, esi`, then the pad loop from `0x00889a83`
  (`push 0` …, `inc esi; cmp esi, 2; jl 0x00889a83`). The first pad runs through the trampoline;
  the second jumps to `+3`.
- `updateFFX`: `push ebp; mov ebp, esp`, then the update loop from `0x00822843`
  (`cmp [0x0133c930], 0` … `jne 0x00822843` at `0x00822876`) while `g_VoiceHold` is not 0.

Both minidumps of the run match this. In one, the fault is at `TkUpdateScanControler+3` (`07`, `pop
es`, from the `jmp`'s address bytes: a general-protection fault, reported as a read of
`0xffffffff`) with `esi` = 1, the loop counter after the first pad, and the stack as it is after
the first call. In the other, the fault is at `updateFFX+8`: from `+3` the `jmp`'s bytes ran as
`stosd; add al, 0x30; leave`, then the game's own `pop ebp`, then `add [eax], eax` with `eax` = 0x30.
Which one crashes first depends on whether the first frame ran more than one update. In both dumps
the crashed function starts with MinHook's `jmp` followed by the game's own bytes, and every other
thread is waiting in the system (so no hook was being installed at the time).

No other function the mod hooks has a branch into its first 5 bytes. A scan of `.text` for `jmp`,
`jcc` and `call` to bytes 1 to 4 of every hooked function finds only the two above, and no
absolute address points there. The fix:

- **Call-site hooks.** `updateFFX` has one caller (`0x0042f5b6`) and `TkUpdateScanControler` two.
  The mod checks that each is `e8 rel32` to the function and points the `rel32` at its own entry,
  which calls the function. The prologues stay untouched.
- **Checked before install.** For every function hooked through Fahrenheit the mod knows its length
  (to the next function in `ffx.sym`) and scans it for `jmp`/`jcc` (rel8 or rel32) into bytes 1
  to 4, at every offset, without decoding (`CodeScan`). One hit and nothing is installed. `loop`
  and `jcxz` are left out: MSVC does not emit them, and a stray `e0` in `MsCameraProcess` would be
  a false hit.
- **Checked after install.** The first time a hook runs, its next link must exist, must not be the
  function itself, the function must start with `e9`, and the link must start with the function's
  first byte (a trampoline) or `e9` (another hook over it, as on `Sg_MainLoop`, which Fahrenheit's
  game-loop events hook too). Otherwise the mod passes through from then on and logs why.

### The camera

After each update that ticked, `graphicCameraSetWorldMatrix(&g_CameraMatrix)` (from `Cam_Apply`) is
the tick's camera. The mod records it with `g_ScreenMatrix`. Then, and on every Redraw, it writes the
camera between the last two ticks into both globals before the call goes on, so the renderer, the
effects' fill matrices and the rest of the draw use it. `Sg_MainLoop`'s hook puts the true matrices
back before the next update. A Repeat frame has no draw, so the mod sets the globals and calls
`graphicCameraSetWorldMatrix` itself. The camera matrix is taken apart into a scale on the view's
axes (or the world's), a rotation and the eye. The rotation is slerped, the eye and scale lerped.
The screen matrix is lerped element by element. A move over 100 units or a turn over 45° in one tick
(both configurable), or a screen matrix that changed by a quarter, is a cut and is drawn as it is.
The mod does not interpolate during a movie, with the part matrix on (`0x012fb8f9`), or for a
second screen's camera (one that is not the recorded one).

### Effects between ticks

**Why they ran fast.** The draw's effect loop (`Sg_MainLoop`, `0x0082141d`..`0x008214ca`; port
`m5main_loop.rs` `draw`) runs once per game update, Redraws included, and per pass calls
`op_loop_ffx_after`, `op_gs1_transfer`, `yiCallFieldParticle` (`pppFpLoop` `0x00729ab0`, the map's
particles: the pyreflies), `MsEffectProcess(1)` (`op_effect_run_after`), `pppPartLoop` and
`op_ot_draw`. None of these only draws. `pppFpLoop` counts each effect's countdown down and runs
its `ppp` program (`pppRunPartFp`), which moves the particles and writes their vertices in the
same pass; `pppPartLoop` steps its parts' countdowns; `op_loop_ffx_after` steps `g_OpLoopCount`
and the effects' pad and random latches. The game itself treats the loop as per tick: with exec
frames n it runs n passes (`n = exec ? exec : 1`). So on a Redraw frame the effects stepped once
more, about 200 / 30 = 6.7 times too fast at 200 fps.

**Why holding them flickered.** The Remaster hands the effects to PhyreEngine as
`VFXDynamicGeometry` objects, and each one is drawn only in a frame where something registers it:

- `VfxGeometry_Register` (vtable `+8`; reached from `graphicVFXUpdate`, `graphicVFXUpdateWithType`
  and the other `graphicVFX*` updates) updates the geometry's render data and puts it on the
  scene's VFX list (ffx-rust `docs/formats/effects-draw.md`, "The VFX lists of `PhyreScene`").
- `graphicClearSpecialRenderList` empties those lists at the start of every `Sg_MainLoop`.
- The vertices live in the dynamic-geometry memory (`g_DynGeoFillBuffer`), two buffers that flip
  once per drawn frame (`graphicDynGeoMemManagerSwapBuffer` from `FFXApplication::afterGraphicSync`).
  The flip resets the new buffer and `ClassVFXRenderDataTable_ClearVFXDrawData` clears every
  segment's entry for it (`+0x34 + 0x1c n`), so a geometry nobody wrote this frame has no vertices.
- The Esc menu shows a still picture with its effects by stopping both: `animate` skips the update
  and `afterGraphicSync` stops swapping once the menu is up.

**What the mod does (Replay, the default).** On a tick, while each of the six calls runs (the
effect loop's five and `MsEffectProcess(0)` in the update), the mod records, in order, every
`VfxGeometry_Register(geometry, flags)` with `g_VfxCurrentType`, every `graphicVFXSyncPath` (the
list's draw paths) and every `PhyreScene_addVFXDrawEvent` (16-byte screen fade, buffer copy,
character and lighting events). A new `Sg_MainLoop` with ticks starts empty, and each pass of the
effect loop empties the loop's five, so the last pass is what is kept. On a Redraw frame the six
calls and `op_loop_ffx_after` do not run. In each one's place the mod replays its record:

1. The geometry must still be in `g_VFXRenderDataTable` (its `std::map` walked once per update, as
   `ClearVFXDrawData` walks it). A geometry destroyed since is left out (counted as "gone").
2. Each segment of its render data with vertices in the previous buffer and none in the current
   one gets a block in the current buffer (`graphicDynGeoMemManagerGetBufferForSegment`, which
   records it as the segment's current arrays) and its five arrays copied: positions (12 bytes a
   vertex), colours (16), normals (12), uvs (8), indices (2), with the counts the previous buffer
   recorded.
3. `VfxGeometry_Register` runs again with the recorded flags and type; sync paths and draw events
   run again in between, with the event's list position recomputed by the game.

The effects therefore step 30 times a second and are drawn on every frame, each at its last tick's
shape. Whether they follow the interpolated camera depends on the space of their vertices: the
field particles' loop sets Phyre's view projection from the camera (`UpdatePhyreViewProjectionMatrix`)
and keeps the camera's inverse as its fill matrix, which points to world space, but this is not
checked in the game. If they are in screen space, an effect trails a moving camera by up to one
tick. Settings: **Run** gives the old
behaviour (the five run on every frame), **Hold** holds them without a replay (they then flicker).

What it does not cover: state an effect sets only while it runs (the map particles' point lights,
`pppFpPointLightProc`), particles built facing the tick's camera, and the extra passes of exec
frames above ×4 per frame. Repeat frames are not replayed: there the flip still clears the
segments, so effects may be missing on Repeat frames (as before).

### Characters between ticks

**Why slow motions were choppy.** Only the camera was interpolated; with `Ch_CalcMiddle` held, a
character's pose changed 30 times a second against a 200 fps camera.

**Where the draw gets them.** `Ch_DrawMain` builds each character's world matrix as `scale(1000) ×
+0x1d0` (`FFXVu0MulMatrix(out, a, b)` is `cdc_Mul4x4`, which computes `b × a` in row-vector
form), a part of a model that is not skinned as `base element × scale × +0x1d0`, and hands them to
`graphicCharacterInstanceSetWorldMatrix(list, m)` / `…ByMdlID(list, m, model)` for the instance
lists `+0x830` and `+0x838`. The bones are not in `Ch_DrawMain`: `Ch_CalcElement` (inside
`Ch_CalcMiddle_Batch`, on the job workers) copies them into every renderer instance with
`graphicCharacterInstanceSetSkeletonMatrices` (`0x0063be60`): instance list `[list] & 0x7fffffff`
instances at `[list + 4]`; per instance `[[instance] + 0x18] & 0x7fffffff` matrices of 64 bytes at
`[instance + 0xc]`, remapped from the character's bone array.

**What the mod does.** After `Ch_CalcMiddle` returns in a game update with ticks, each active
character's `+0x1d0` (keyed by its slot, identity = its id and instance list) and each of its
instances' bone matrices (keyed by the instance, identity = the array and count) go into a two-tick
history. Nothing is written into the bone arrays during the update: `Ch_CalcMain` ends by queueing
the ordinary characters' bone jobs (`JobSchedule_Ch_CalcMiddle_Batch`, pass 0) without waiting, and
only `Ch_CalcMiddle`'s first call (`JobScheduleWait_Ch_CalcMiddle_Batch` at `0x008333ad`) waits for
them. (The first version put the last tick's bones back on `Ch_CalcMiddle`'s entry, raced those
jobs, and recorded old poses as new: in the 2026-11-07 test, characters were choppy and half a
character could stand still for a second while its pass-1 parts moved.) Instead, when recording,
an instance whose array still holds exactly what the mod last wrote was not updated by the game
this tick, so its last true bones are written back and recorded again. In each
`Ch_DrawMain` (ticks and Redraws), for a character with two consecutive samples of the same
identity and no warp:

- the correction `+0x1d0⁻¹ × lerp(+0x1d0)` (rotation slerped, translation and scale lerped) is
  applied on the right of every world matrix `Ch_DrawMain` hands the renderer for its lists
  (`+0x1d0` itself stays true: the cloth, the cull, the shadows and the game read it);
- every bone matrix of its instances is written as the slerp/lerp of its two samples (the poses of
  each tick taken apart once, when recorded); the rest stay the tick's own.

A warp is a move over 50 units or a turn over 120° in one tick (settings), a mirror that flips, or
a matrix that cannot be taken apart and changed by a quarter. A character missing from the last
tick, a new identity in the slot or a new map (`g_CurrentMapNo`) starts again. A `+0x1d0` that
changed since the tick (magic `0x275`'s matrix swap) is drawn as it is.

**The cloth.** `Ch_DrawMain` queues `Ch_Cloth_PerCharacter` (`Apply_Envelope`, `Sg_ClothStep`,
`Sg_ClothPlaceAnchors`) for every cloth character once per game update, after
`Ch_DrawClothClear`, so on Redraw frames the cloth simulated once per drawn frame. The mod skips
both on a Redraw (setting, on). `FFXApplication_render` uploads the cloth's vertex records on every
drawn frame (`graphicCharacterInstanceSetClothVertexBuffers`), so the cloth keeps its last tick's
shape, as on a Repeat frame. It is not interpolated.

### Other things that advance per drawn frame

What each `Sg_MainLoop` runs once besides the ticks, and what the mod does with it on a Redraw:

| What | Where | On a Redraw |
|---|---|---|
| The effects | the effect loop, above | held, replayed |
| The cloth | `Ch_DrawMain` → `Ch_Cloth_PerCharacter` | held (new) |
| Bones, motion events, mouths, texture animations of the characters | `Ch_CalcMiddle` | held; bones interpolated |
| Field animation (the map's texture animation frames, moving parts) | `yiAnimMain` | held. `yiRender_draw` only applies the frame |
| Fades | `Sg_DrawFilter` | drawn, counters put back |
| The Eternal Calm copyright | `Sg_MainLoop` | counter put back |
| The video | `graphicVideoUpdate` | held; movies Repeat |
| Pad scan, controls, v-blank counters, sync clock | `TkUpdateScanControler`, `Sg_ControlMain`, `Sg_FrameSync` | held |
| Sound queue (`TkSndUpdate2`, after `AtelDrawNoTrans`), `graphicMenuUpdate`, `SaveManagerUpdate` | the draw | run: real-time or idempotent as far as read; not checked in the game |
| Screen effects (`yiDrawScrEffect`), the dream effect (`drawDreamEffect`), the menu water | the draw | run: draws; whether any keeps a counter is not checked |

### Open questions for the in-game test

- Whether a replayed effect looks like its tick (vertices, colour, draw order) and whether any
  effect still flickers or vanishes between ticks ("gone" in the window's Live section counts
  geometries destroyed since the tick).
- Whether interpolated bones look right on every model (skinned and not, the shadow instance
  `+0x838`, Tidus's and Yuna's cloth over an interpolated body).
- The character warp thresholds in world units.
- Whether a Redraw frame looks like the tick frame before it: the field, message windows, battle UI.
- Whether a Repeat frame (no update) shows the last picture or a broken one, and whether the
  camera can move there (`graphicCameraSetWorldMatrix` alone, with the rest of the frame left).
- The cut thresholds in world units.
- How many ticks a second one core manages at ×100.
