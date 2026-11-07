// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace Ffx.Frames;

/// <summary>What the effects do on a Redraw frame (<see cref="FrameSettings.Effects"/>).</summary>
public enum EffectsMode {
    /// <summary>
    ///     The effects' programs are held, and what they handed the renderer at the last tick is handed to it again: the
    ///     effects stay at the game's speed and are drawn on every frame.
    /// </summary>
    Replay,
    /// <summary>The effects' programs run on every drawn frame, as before this setting existed: drawn every frame, but too fast.</summary>
    Run,
    /// <summary>The effects' programs are held and nothing is drawn for them: right speed, but they are missing on frames without a tick.</summary>
    Hold,
}

/// <summary>What a hooked effect call does in this frame.</summary>
public enum EffectAction {
    /// <summary>The game's call runs (tick frames, and Redraw frames in <see cref="EffectsMode.Run"/>).</summary>
    Run,
    /// <summary>Skipped.</summary>
    Hold,
    /// <summary>Skipped, and its draws from the last tick are handed to the renderer again.</summary>
    Replay,
}

/// <summary>
///     The calls of <c>Sg_MainLoop</c> that run the effects' programs (the PS2's <c>ppp</c> and <c>opus</c> code, recompiled):
///     each steps its effects (integer countdowns, velocities, the random draws) and hands the result to the renderer in
///     the same pass, so they cannot run again between ticks without speeding the effects up.
/// </summary>
public enum EffectSlot {
    /// <summary><c>MsEffectProcess(0)</c>, in the world update.</summary>
    Update,
    /// <summary><c>op_gs1_transfer</c>, the first call of each pass of the draw's effect loop that draws.</summary>
    Gs1Transfer,
    /// <summary><c>yiCallFieldParticle</c> (<c>pppFpLoop</c>): the map's particles.</summary>
    FieldParticles,
    /// <summary><c>MsEffectProcess(1)</c>: battle, event and character effects (<c>op_effect_run_after</c>).</summary>
    EffectsAfter,
    /// <summary><c>pppPartLoop</c>: the <c>ppp</c> parts.</summary>
    PartLoop,
    /// <summary><c>op_ot_draw</c>: the effects' ordering tables.</summary>
    OtDraw,
}

/// <summary>One thing an effect handed the renderer.</summary>
public enum EffectDrawKind {
    /// <summary><c>VfxGeometry_Register(geometry, flags)</c> (<c>VFXDynamicGeometry</c> vtable <c>+8</c>): puts the geometry on the scene's VFX list for this frame.</summary>
    Register,
    /// <summary><c>graphicVFXSyncPath()</c>: starts the next path of the VFX list (its draw order).</summary>
    SyncPath,
    /// <summary><c>PhyreScene_addVFXDrawEvent(scene, event)</c>: a 16-byte draw event (screen fade, buffer copy, a character, …) at this point of the list.</summary>
    DrawEvent,
}

/// <param name="Kind">Which call.</param>
/// <param name="Target">The geometry (<see cref="EffectDrawKind.Register"/>) or the scene (<see cref="EffectDrawKind.DrawEvent"/>).</param>
/// <param name="Flags">The register flags (type in the low bits, bit 30 no update, bit 31 the draw flag).</param>
/// <param name="VfxType"><c>g_VfxCurrentType</c> at the call.</param>
/// <param name="E0">The draw event's words.</param>
public readonly record struct EffectDraw(EffectDrawKind Kind, nint Target = 0, int Flags = 0, byte VfxType = 0,
    int E0 = 0, int E1 = 0, int E2 = 0, int E3 = 0);

/// <summary>Which effect calls run, are held or replayed (pure; <see cref="FrameHooks"/> carries it out).</summary>
public static class EffectGate {
    /// <summary>What an effect call does: it always runs outside Redraw frames.</summary>
    public static EffectAction Decide(EffectsMode mode, bool redraw) => !redraw ? EffectAction.Run : mode switch {
        EffectsMode.Run => EffectAction.Run,
        EffectsMode.Hold => EffectAction.Hold,
        _ => EffectAction.Replay,
    };

    /// <summary>Whether <c>op_loop_ffx_after</c> (the effects' per-pass counters, pads and random latches) runs.</summary>
    public static bool RunLoopAfter(EffectsMode mode, bool redraw) => Decide(mode, redraw) == EffectAction.Run;
}

/// <summary>
///     The effects' draws of the last tick, per <see cref="EffectSlot"/>, in call order. Filled on the game thread while a
///     slot's call runs on a frame with a tick; replayed in its place on Redraw frames.
/// </summary>
public sealed class EffectRecorder {
    private readonly List<EffectDraw>[] _slots = new List<EffectDraw>[Enum.GetValues<EffectSlot>().Length];
    private int _recording = -1;

    public EffectRecorder() {
        for (int i = 0; i < _slots.Length; i++) _slots[i] = [];
    }

    /// <summary>The slot being recorded, or null.</summary>
    public EffectSlot? Recording => _recording < 0 ? null : (EffectSlot)_recording;

    /// <summary>Entries held across all slots.</summary>
    public int Count {
        get {
            int n = 0;
            foreach (List<EffectDraw> s in _slots) n += s.Count;
            return n;
        }
    }

    /// <summary>Starts recording <paramref name="slot"/>, replacing what it held: the last call of a slot in a frame is the one replayed.</summary>
    public void Begin(EffectSlot slot) {
        _slots[(int)slot].Clear();
        _recording = (int)slot;
    }

    /// <summary>Stops recording.</summary>
    public void End() => _recording = -1;

    /// <summary>
    ///     A new pass of the draw's effect loop (<c>op_loop_ffx_after</c>): the draw slots start empty, so one the pass does
    ///     not reach (the field particles while they are off) replays nothing instead of an older tick's draws.
    /// </summary>
    public void BeginDrawPass() {
        for (int i = (int)EffectSlot.Gs1Transfer; i < _slots.Length; i++) _slots[i].Clear();
    }

    /// <summary>Forgets everything (the mod switched off, a game update without effects).</summary>
    public void Clear() {
        foreach (List<EffectDraw> s in _slots) s.Clear();
        _recording = -1;
    }

    /// <summary>Adds a draw to the slot being recorded; false (and nothing kept) when none is.</summary>
    public bool Add(in EffectDraw draw) {
        if (_recording < 0) return false;
        _slots[_recording].Add(draw);
        return true;
    }

    /// <summary>The draws of <paramref name="slot"/>, in order.</summary>
    public IReadOnlyList<EffectDraw> Entries(EffectSlot slot) => _slots[(int)slot];
}

/// <summary>
///     The layout of a VFX render data segment's vertex arrays (<c>graphicDynGeoMemManagerGetBufferForSegment</c>
///     <c>0x0063fdb0</c>; ffx-rust <c>docs/formats/render.md</c>). A segment is <c>0x6c</c> bytes. For each dynamic-geometry
///     buffer <c>n</c> it keeps the counts at <c>+0x2c + 0x1c n</c> (vertices) and <c>+0x30 + 0x1c n</c> (indices) and five
///     arrays: positions (12 bytes a vertex) at <c>+0x34 + 0x1c n</c>, colours (16) at <c>+0x3c</c>, normals (12) at
///     <c>+0x38</c>, uvs (8) at <c>+0x40</c>, <c>u16</c> indices at <c>+0x44</c>.
/// </summary>
public static class DynGeoSegment {
    public const int Size = 0x6c, SlotStride = 0x1c;
    public const int VertexCount = 0x2c, IndexCount = 0x30;

    /// <summary>Each array's offset in buffer 0's slot, and its bytes per vertex (or per index for the last).</summary>
    public static ReadOnlySpan<(int Offset, int Bytes, bool PerIndex)> Arrays => ArrayTable;

    private static readonly (int Offset, int Bytes, bool PerIndex)[] ArrayTable = [
        (0x34, 12, false), (0x3c, 16, false), (0x38, 12, false), (0x40, 8, false), (0x44, 2, true),
    ];

    /// <summary>The offset of <paramref name="field"/> (an offset in buffer 0's slot) in buffer <paramref name="buffer"/>'s slot.</summary>
    public static int At(int field, int buffer) => field + SlotStride * buffer;

    /// <summary>The buffer the frame before used: the buffers flip once per drawn frame, in order.</summary>
    public static int Previous(int current, int count) => (current + count - 1) % count;

    /// <summary>Bytes of one array for <paramref name="vertices"/> and <paramref name="indices"/>; 0 for a count out of range.</summary>
    public static long Bytes((int Offset, int Bytes, bool PerIndex) array, int vertices, int indices) {
        int n = array.PerIndex ? indices : vertices;
        return n is > 0 and <= MaxCount ? (long)n * array.Bytes : 0;
    }

    /// <summary>The most vertices or indices a segment is copied with (the game's largest buffers are far below).</summary>
    public const int MaxCount = 1 << 20;
}
