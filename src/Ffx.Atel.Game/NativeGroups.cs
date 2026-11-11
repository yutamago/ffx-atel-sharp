// SPDX-License-Identifier: MIT

namespace Ffx;

// What each class of natives is for, in a sentence a modder can use: the generated classes (Natives.g.cs,
// tools/gen-natives.cs) only say which native group they hold. The compiler merges these summaries with the
// generated ones.

/// <summary>
///     The game's general natives: waiting, moving and turning a worker, the controller, triggers, text boxes,
///     map changes and more. Many have a plainer form as a worker member or a static class (<see cref="Wait"/>,
///     <see cref="Dialogue"/>, <c>Pos</c>, <c>Turn</c>, <c>Move</c>); use that where there is one.
/// </summary>
public static partial class Common;

/// <summary>Maths natives: sine, cosine, square roots, angles, distances and splines.</summary>
public static partial class Calc;

/// <summary>
///     Natives for what the screen shows around the characters: fades in and out, colour filters, lights and the
///     map's drawing.
/// </summary>
public static partial class Screen;

/// <summary>
///     Natives for a worker's character model: its motions, size, shadow, collision and what is attached to it. Many
///     have a plainer form as a worker member (<c>Motion</c>, <c>Neck</c>, <c>Hidden</c>, <c>Model</c>).
/// </summary>
public static partial class Chr;

/// <summary>
///     The camera natives. <see cref="Camera"/> gives most of them as members (<see cref="Camera.Eye"/>,
///     <see cref="Camera.Focus"/>); use those.
/// </summary>
public static partial class Cam;

/// <summary>
///     Battle natives, for monster and battle scripts: the actors and their stats, commands and targets, damage,
///     battle motions and the end of the battle. The actors' stats are also members: <c>Actors[…].Hp</c>,
///     <c>Self.Hp</c>.
/// </summary>
public static partial class Btl;

/// <summary>Natives for the map's effects: effect runs, animated map parts, background scrolling and fog.</summary>
public static partial class Fx;

/// <summary>Natives that play the game's movies and wait for them.</summary>
public static partial class Movie;

/// <summary>A mix of natives: loading maps, streamed sound and music, the configuration, and debug helpers.</summary>
public static partial class GroupC;

/// <summary>Holds one native, a debug print.</summary>
public static partial class Group9;

/// <summary>Holds one debug native.</summary>
public static partial class GroupD;
