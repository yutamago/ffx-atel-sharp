# Frame timing: what a script frame is, and what a frame-rate mod has to do

How often FFX.exe (Steam HD Remaster) runs its scripts, what else counts in frames, and what that means
for `Wait.Seconds`, an uncapped or monitor-rate frame rate, fast-forward, and waits shorter than a
frame. Static research only: FFX.exe's code (objdump, ffx-rust's `tools/refs.py` cross-references and
`symbols/ffx.sym`) and ffx-rust's ports of the functions involved (`crates/ffx-sg/src/m5main_loop.rs`,
`crates/ffx-sg/src/state.rs` `frame_sync`, `crates/ffx-script/src/vm/bridge/run.rs`). The game was
not run for this. The engine findings are also in ffx-rust (`docs/formats/frame-timing.md`).

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
