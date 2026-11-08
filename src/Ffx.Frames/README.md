# Frames (`yuta.frames`)

A [Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) mod for the Steam HD Remaster of
Final Fantasy X that removes the fixed 30 fps limit without speeding the game up. The game
draws at your monitor's refresh rate, at a rate you choose, or uncapped, with vsync on or off.
The world keeps its 30 ticks a second, and the camera is drawn between ticks (the characters too, as an experimental option). A time scale
gives slow motion and fast-forward (×0.05 to ×100) without drawing more frames.

It is a separate mod from Atel and works with or without it. The research behind it is in
[`docs/research/frame-timing.md`](../../docs/research/frame-timing.md) ("What the Frames mod
does" lists every hook).

**Status: in testing.** The first in-game run crashed on its first frame; that is fixed (see
"Limits"). The second (2026-11-07, about 200 fps) kept the game's speed, but the effects ran too
fast and slow character motions looked choppy; the effects' replay, the character
interpolation and the cloth hold answer that and wait for their own test. Treat the defaults as
a starting point until then.

## Install

Build it (.NET 10):

```bash
dotnet build -c Release src/Ffx.Frames
```

Copy `artifacts/deploy/release/mods/yuta.frames` into your Fahrenheit folder's `mods/`, and add
a line `yuta.frames` to `mods/loadorder`. It was built against Fahrenheit 1.0.0-alpha12.

## Use

- **F9** shows and hides the settings window.
- **Alt+F9** switches vsync on and off.
- A small counter in the top-right corner shows the drawn fps, the world ticks a second (about 30 at
  ×1), vsync and the time scale; "Show the fps counter" in the window turns it off.
- **Ctrl+F9** switches between the fast-forward scale (×10 by default) and ×1.
  **Shift+F9** does the same for slow motion (×0.25). **Ctrl+Shift+F9** goes back to ×1.
  **Ctrl+PgUp** and **Ctrl+PgDn** step through ×0.05 … ×100.
- The settings are saved half a second after a change, in Fahrenheit's state folder:
  `state/global/yuta.frames/Ffx.Frames.FramesModule` (JSON; delete it to start over).
- The log is `logs/<start time>/Ffx.Frames.FramesModule.log` in the Fahrenheit folder.

### Settings

| Setting | Default | What it does |
|---|---|---|
| On | on | Off gives the game back its own timing, limiter and vsync |
| Target | Monitor | **Game** (30 fps, the game's own limiter), **Monitor** (the refresh rate of the window's monitor), **Fixed** (10 to 1000 fps), **Unlimited** (no limiter) |
| Vsync | As the game has it | **On** or **Off** overrides the game's setting for each present. Off with a G-Sync/FreeSync display and a Fixed or Unlimited target gives a variable frame rate |
| Frames between ticks | Redraw | What a frame without a tick does: **Redraw** runs the game's draw again with the world held still, so the camera can move. **Repeat** skips the game's update, as the Esc menu does |
| Interpolate the camera | on | Draws the camera between its last two ticks. The picture is one tick (33 ms) behind. At target Game and ×1 or faster there is nothing between ticks: the camera and the characters are drawn as the tick has them |
| … the characters | off | **Experimental, off until checked in the game.** Draws each character's place, facing, bones (its animation) and cloth (hair, Tidus's shorts, Auron's coat) between its last two ticks, like the camera. A character that jumps further than 50 units or turns more than 120° in one tick, a new one, or one whose model changed is drawn as it is |
| Effects between ticks | Replay | **Replay**: the effects' programs run only on ticks, and on the frames between, what they drew at the last tick is drawn again: right speed, drawn every frame. **Run**: they run on every drawn frame (too fast at a high frame rate). **Hold**: they run only on ticks and are missing between them (they flicker) |
| Time scale | ×1 | Game time per real second. Voice-synced scenes, movies and the scenes the game won't speed up stay at ×1 |
| Mute above ×1 | on | Sound off while faster than ×1, as the booster does |
| Keep the booster's limits | on | Also ×1 where the PC booster won't speed up (its maps and spells, the sphere grid, a load) |

Advanced: the most real time one frame counts (0.3 s, as the game), the most ticks one frame
runs (100), how many whole game updates a frame runs before it uses exec frames (4), for
Redraw frames which parts are held still (the cloth among them), whether the characters' bones
and cloth are interpolated or only their place (the cloth needs to be held), and the characters' warp thresholds. The window's **Live** section shows the drawn
frame rate, the world's ticks a second (about 30 at ×1, whatever the frame rate), why the scale
is held at ×1, and the camera's last move and cuts (to tune the cut thresholds). "The game's
instant camera sets are cuts" (on) also cuts when a script or magic file sets the camera at once,
however little it moved.

## How it works

The game runs its world in ticks of 1/30 s. Each drawn frame used to run at least one tick, so
drawing faster ran the game faster. The mod changes that:

1. **The limiter.** `Game_Main`'s frame limiter waits the mod's interval instead of 2 vsyncs.
2. **The tick accumulator.** Each drawn frame adds its real time × the time scale. It runs as
   many whole ticks as fit, 0 included, and carries the rest, like Unity's `FixedUpdate`.
3. **Frames without a tick.** Redraw runs the game's update with 0 ticks and holds what would
   still move: pad scan, controls, the v-blank count, the camera, the effects, the characters'
   bones and motion events, the cloth (the hair is cloth too), field animation, fade counters, video. The effects'
   programs step and draw in one pass, so on these frames the mod draws again what they drew at
   the last tick instead of running them. Menus, pauses, loads and movies always use Repeat,
   because their logic runs once per update.
4. **Interpolation.** After each tick the mod records the camera and screen matrices, each
   character's matrix and bone matrices, and its cloth's vertices. For each draw it gives the
   renderer the camera and the characters between the last two ticks: rotations slerped,
   positions lerped, the cloth's vertices blended and placed with the character as drawn. A jump
   bigger than the cut or warp thresholds is drawn as it is. When the camera cuts to a new shot
   (by the thresholds, or the game sets its camera at once), the characters and their cloth are
   drawn as the tick has them too, so they do not slide in from the last shot.
5. **Time scale.** Above ×4 per frame, ticks go through the game's exec frames (up to 99 ticks
   per update) instead of whole updates, so ×100 costs 3,000 ticks a second but no extra draws.

## Limits

- **Effects, the 2D layers and blob shadows move at 30 Hz.** Effects keep their
  game speed and are drawn on every frame, but each shows its last tick's shape between ticks.
  Particles the game builds facing the camera were built for the tick's camera; with the
  camera interpolated they can trail it by up to one tick. A character's round shadow stays at
  its last tick's place. Without character interpolation the cloth and hair move at 30 Hz too.
- **What the effects replay cannot cover.** The replay re-sends the effects' geometry, the order
  of their draw paths and their draw events (screen fades, buffer copies). Something an effect
  sets only while it runs keeps its last value between ticks: the point lights the map's
  particles cast (`pppFpPointLightProc`), for example, are not set again. With exec frames (above ×4 per frame) a tick frame draws the effects once
  per tick, as the game does, but a frame between ticks draws them once. If an effect still
  flickers or vanishes between ticks, set **Effects between ticks** to Run and report which one.
- **Character interpolation is experimental and off by default.** In its first in-game test
  (2026-11-07) characters were choppier with it and half a character could stand still for a
  second; turning it off fixed that. The cause (it raced the game's bone jobs) is fixed, and the
  second test (2026-11-07, ×0.1) had smooth bones, but the hair trailed and moved only on
  ticks: hair is cloth, which was not interpolated. The cloth now is, not yet checked in the
  game. Without it, characters move at 30 Hz against the smooth camera.
- **Characters drawn by other paths** (a magic file's situation pass, the effects' character
  draws) stay at 30 Hz.
- **Fast-forward costs CPU.** Every tick is simulated. ×100 is 3,000 ticks a second, which may
  be more than one core can do in heavy scenes. The game then runs slower than asked but stays
  responsive ("most ticks per frame").
- **Above ×4 per frame**, exec frames run several ticks per update. What runs once per update
  (field animation, fades, the characters' middle step, the video) then falls behind, as with
  the game's debug fast-forward.
- **Audio, movies, loads and voice-synced scenes run in real time**, whatever the scale.
- **Uncapped with vsync off** keeps one CPU core busy.
- Sound and subtitles: only the sound is muted when fast. The booster also mutes the battle
  subtitles; this mod does not.
- **Some functions cannot be hooked the usual way.** Fahrenheit's hooks overwrite a function's first
  5 bytes. `updateFFX` and `TkUpdateScanControler` each have a loop that jumps back to their third
  byte, and hooking them that way crashed the game on its first frame. The mod hooks these two at
  their calls instead. Before installing anything it checks every other hooked function for such a
  jump, and each call site for the expected `call`. If a check fails it installs nothing and logs
  why. Once Fahrenheit has applied the hooks, the first call through each one checks its chain. A
  bad chain switches the mod to pass-through. The details are in frame-timing.md ("Hooking: why
  the first run crashed").
