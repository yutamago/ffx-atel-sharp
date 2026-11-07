# Frames (`yuta.frames`)

A [Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) mod for the Steam HD Remaster of
Final Fantasy X that removes the fixed 30 fps limit without speeding the game up. The game
draws at your monitor's refresh rate, at a rate you choose, or uncapped, with vsync on or off.
The world keeps its 30 ticks a second, and the camera is drawn between ticks. A time scale
gives slow motion and fast-forward (×0.05 to ×100) without drawing more frames.

It is a separate mod from Atel and works with or without it. The research behind it is in
[`docs/research/frame-timing.md`](../../docs/research/frame-timing.md) ("What the Frames mod
does" lists every hook).

**Status: not yet tested in the game.** The test plan is in the pull request that added the
mod. Treat the defaults as a starting point until that test is done.

## Install

Build it (.NET 10):

```bash
dotnet build -c Release src/Ffx.Frames
```

Copy `artifacts/deploy/release/mods/yuta.frames` into your Fahrenheit folder's `mods/`, and add
a line `yuta.frames` to `mods/loadorder`. It was built against Fahrenheit 1.0.0-alpha12.

## Use

- **F9** shows and hides the settings window.
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
| Interpolate the camera | on | Draws the camera between its last two ticks. The picture is one tick (33 ms) behind |
| Time scale | ×1 | Game time per real second. Voice-synced scenes, movies and the scenes the game won't speed up stay at ×1 |
| Mute above ×1 | on | Sound off while faster than ×1, as the booster does |
| Keep the booster's limits | on | Also ×1 where the PC booster won't speed up (its maps and spells, the sphere grid, a load) |

Advanced: the most real time one frame counts (0.3 s, as the game), the most ticks one frame
runs (100), how many whole game updates a frame runs before it uses exec frames (4), and, for
Redraw frames, which parts are held still. The window's **Live** section shows the drawn
frame rate, the world's ticks a second (about 30 at ×1, whatever the frame rate), why the scale
is held at ×1, and the camera's last move (to tune the cut thresholds).

## How it works

The game runs its world in ticks of 1/30 s. Each drawn frame used to run at least one tick, so
drawing faster ran the game faster. The mod changes that:

1. **The limiter.** `Game_Main`'s frame limiter waits the mod's interval instead of 2 vsyncs.
2. **The tick accumulator.** Each drawn frame adds its real time × the time scale. It runs as
   many whole ticks as fit, 0 included, and carries the rest, like Unity's `FixedUpdate`.
3. **Frames without a tick.** Redraw runs the game's update with 0 ticks and holds what would
   still move: pad scan, controls, the v-blank count, the camera, the effects' update,
   characters' bones and motion events, field animation, fade counters, video. Menus, pauses,
   loads and movies always use Repeat, because their logic runs once per update.
4. **Interpolation.** After each tick the mod records the camera and screen matrices. For each
   draw it gives the renderer the camera between the last two ticks: the rotation slerped, the
   position lerped. A jump bigger than the cut thresholds is a cut and is drawn as it is.
5. **Time scale.** Above ×4 per frame, ticks go through the game's exec frames (up to 99 ticks
   per update) instead of whole updates, so ×100 costs 3,000 ticks a second but no extra draws.

## Limits

- **Characters, effects and the 2D layers are not interpolated.** They move at 30 Hz, as
  before. With the camera interpolated, a character the camera follows can look like it shakes
  slightly against the smooth background. If that bothers you, turn interpolation off: you
  keep the high frame rate and the correct speed. Character interpolation is planned
  (frame-timing.md).
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
