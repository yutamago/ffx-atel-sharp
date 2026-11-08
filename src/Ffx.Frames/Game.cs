// SPDX-License-Identifier: MIT

using System;
using System.Numerics;

namespace Ffx.Frames;

/// <summary>
///     FFX.exe's globals the mod reads and writes (Steam HD Remaster; virtual addresses for the image base
///     <c>0x00400000</c>, rebased at run time). Names from ffx-rust's <c>symbols/ffx.sym</c>, Fahrenheit's where it
///     has one, and docs/research/frame-timing.md.
/// </summary>
/// <remarks>Game thread only, except <see cref="Renderer"/>'s vsync byte (render thread, inside its own call).</remarks>
internal sealed unsafe class Game(nint moduleBase) {
    public const uint ImageBase = 0x00400000;

    private T* At<T>(uint va) where T : unmanaged => (T*)(moduleBase + (nint)(va - ImageBase));

    /// <summary>A virtual address in this process.</summary>
    public nint Address(uint va) => moduleBase + (nint)(va - ImageBase);

    /// <summary>A byte of the loaded image by RVA (headers and sections, as the loader maps them).</summary>
    public byte ImageByte(uint rva) => *(byte*)(moduleBase + (nint)rva);

    /// <summary>The loaded image's size (<c>SizeOfImage</c> in its optional header), 0 when the headers are not a PE32.</summary>
    public uint ImageSize {
        get {
            if (*(ushort*)moduleBase != 0x5a4d) return 0;
            uint nt = *(uint*)(moduleBase + 0x3c);
            if (nt > 0x1000 || *(uint*)(moduleBase + (nint)nt) != 0x00004550) return 0;
            return *(uint*)(moduleBase + (nint)nt + 24 + 56);
        }
    }

    /// <summary>The relocated dwords of the loaded image (<see cref="PeRelocations"/>), read once.</summary>
    public uint[] Relocations => _relocations ??= ImageSize is var size and > 0 ? PeRelocations.Read(ImageByte, size) : [];
    private uint[]? _relocations;

    /// <summary>An image-relative offset, as Fahrenheit's <c>FhCallLocation("FFX.exe", offset)</c> takes it.</summary>
    public static nint Offset(uint va) => (nint)(va - ImageBase);

    /// <summary><c>g_FrameVsyncs</c> (<c>0x00c30e88</c>): vsyncs per frame, 2 in the world, 1 in the main menu.</summary>
    public int FrameVsyncs => *At<int>(0x00c30e88);

    /// <summary><c>g_VoiceHold</c> (<c>0x012fb808</c>): game updates still to run this frame; <c>updateFFX</c> repeats <c>Sg_MainLoop</c> while it is not 0.</summary>
    public ref int VoiceHold => ref *At<int>(0x012fb808);

    /// <summary><c>Sg_FrameSync</c>'s two carries (<c>0x012fb7f8</c>, <c>0x012fb7fc</c>, <c>f32</c> seconds).</summary>
    public void ClearCarries() {
        *At<float>(0x012fb7f8) = 0;
        *At<float>(0x012fb7fc) = 0;
    }

    /// <summary><c>g_SyncOn</c> (<c>0x012fb8a0</c>): a voice-synced cut scene; <c>Sg_FrameSync</c> paces ticks by the sync clock.</summary>
    public bool SyncOn => *At<int>(0x012fb8a0) == 1;

    /// <summary><c>g_WaitFramesByTime</c> (<c>0x0112a008</c>): a movie plays and waits count its frames.</summary>
    public bool WaitFramesByTime => *At<int>(0x0112a008) != 0;

    /// <summary><c>g_MovieFading</c> (<c>0x01340824</c>; <c>Sg_MainLoop</c>'s scene request).</summary>
    public bool MovieFading => *At<int>(0x01340824) != 0;

    /// <summary>The movie byte (<c>0x012fb8fb</c>): the frame draws a movie picture instead of the world.</summary>
    public bool MovieOn => *At<byte>(0x012fb8fb) != 0;

    /// <summary>The part-matrix byte (<c>0x012fb8f9</c>): the draw takes its camera from the effects' part matrix.</summary>
    public bool PartMatrix => *At<byte>(0x012fb8f9) != 0;

    /// <summary>The exec frames (<c>0x00c496d4</c>, signed byte): ticks per <c>Sg_MainLoop</c>, what <c>Sg_GetCurExecFrames</c> returns (unless its debug bytes <c>0x013007b1</c>..<c>b3</c> are set).</summary>
    public ref sbyte ExecFrames => ref *At<sbyte>(0x00c496d4);

    /// <summary><c>g_CurrentMapNo</c> (<c>0x012fbc40</c>).</summary>
    public int Map => *At<int>(0x012fbc40);

    /// <summary><c>g_SaveMachineBusy</c> (<c>0x00ce7310</c>): a load is under way.</summary>
    public bool SaveBusy => *At<int>(0x00ce7310) != 0;

    /// <summary>
    ///     The PC booster's factor this frame: <c>g_SpdCtrlScales[level]</c> (<c>0x00c38e1c</c>, level
    ///     <c>0x00ce82b4</c>) while <c>g_SpdCtrl_b8</c> (<c>0x00ce82b8</c>, it scaled at its last call) is set, else 1.
    /// </summary>
    public double Booster {
        get {
            if (*At<int>(0x00ce82b8) == 0) return 1;
            int level = *At<int>(0x00ce82b4);
            if (level is <= 0 or > 2) return 1;
            float f = At<float>(0x00c38e1c)[level];
            return f is > 0 and <= 16 ? f : 1;
        }
    }

    /// <summary><c>g_SpdCtrl_Ready</c> (<c>0x00ce82bc</c>, byte): <c>SpdCtrl_Init</c> ran; the booster asks its exclusions only then.</summary>
    public bool BoosterReady => *At<byte>(0x00ce82bc) != 0;

    /// <summary>The booster's scale is on (it muted the sound itself).</summary>
    public bool BoosterOn => *At<int>(0x00ce82b8) != 0;

    /// <summary><c>g_CameraMatrix</c> (<c>0x02311440</c>): world to view, 4×4 <c>f32</c> rows.</summary>
    public ref Matrix4x4 CameraMatrix => ref *At<Matrix4x4>(0x02311440);

    /// <summary><c>g_ScreenMatrix</c> (<c>0x02311480</c>): view to screen.</summary>
    public ref Matrix4x4 ScreenMatrix => ref *At<Matrix4x4>(0x02311480);

    public nint CameraMatrixAddress => Address(0x02311440);

    /// <summary>
    ///     <c>Sg_DrawFilter</c>'s state (<c>0x01300700</c>..<c>0x0130075f</c>): its flag word and the 5 fade slots at
    ///     <c>0x01300708</c> (mode byte, colour, i16 counter at <c>+4</c>), which it steps each time it draws them.
    /// </summary>
    public const uint FadeState = 0x01300700, FadeStateSize = 0x60;

    public void SaveFades(byte* into) => System.Buffer.MemoryCopy(At<byte>(FadeState), into, FadeStateSize, FadeStateSize);

    public void RestoreFades(byte* from) => System.Buffer.MemoryCopy(from, At<byte>(FadeState), FadeStateSize, FadeStateSize);

    /// <summary>The Eternal Calm copyright's frame count (<c>0x012fb7c0</c>), stepped once per <c>Sg_MainLoop</c>.</summary>
    public ref int CalmFrames => ref *At<int>(0x012fb7c0);

    // ---- Effects (EffectReplay; docs/research/frame-timing.md, "Effects between ticks"). ----

    /// <summary><c>g_PhyreScene</c> (<c>0x00ccb9d8</c>): the scene whose VFX lists and draw events the effects fill.</summary>
    public nint PhyreScene => *At<nint>(0x00ccb9d8);

    /// <summary><c>g_VfxCurrentType</c> (<c>0x00c322aa</c>, byte): <c>graphicVFXSetCurrentVFXType</c>'s type, which the draw loop sets around the field particles.</summary>
    public ref byte VfxCurrentType => ref *At<byte>(0x00c322aa);

    /// <summary>
    ///     <c>g_VFXRenderDataTable</c> (<c>0x00ccca6c</c>): the <c>ClassVFXRenderDataTable</c>, whose first word is the head
    ///     of an MSVC <c>std::map</c> from instance key to <c>VFXDynamicGeometry</c> (node: <c>+0</c> left, <c>+4</c> parent,
    ///     <c>+8</c> right, byte <c>+0xd</c> nil, <c>+0x18</c> the geometry), as <c>ClassVFXRenderDataTable_ClearVFXDrawData</c>
    ///     walks it.
    /// </summary>
    public nint VfxRenderDataTable => *At<nint>(0x00ccca6c);

    /// <summary>
    ///     The dynamic-geometry memory (<c>g_DynGeoFillBuffer</c>, <c>0x00cdedf0</c>): a manager whose first word is the
    ///     current pool, which holds <c>+4</c> the current buffer and <c>+8</c> the buffer count (2). The buffer flips once per
    ///     drawn frame (<c>graphicDynGeoMemManagerSwapBuffer</c> from <c>FFXApplication::afterGraphicSync</c>).
    /// </summary>
    public bool TryDynGeoBuffers(out int current, out int count) {
        current = count = -1;
        nint manager = *At<nint>(0x00cdedf0);
        if (manager == 0) return false;
        nint pool = *(nint*)manager;
        if (pool == 0) return false;
        current = *(int*)(pool + 4);
        count = *(int*)(pool + 8);
        return current >= 0 && count >= 2 && current < count;
    }

    /// <summary><c>g_GraphicSkipFrame</c> (<c>0x012fb7d0</c>): the VFX calls do nothing while it is set.</summary>
    public bool GraphicSkipFrame => *At<int>(0x012fb7d0) != 0;

    // ---- Characters (CharacterInterpolator). ----

    /// <summary><c>g_ChrCount</c> (<c>0x023c44e0</c>) and the character array (<c>0x023c44e4</c>, <see cref="ChrSize"/> bytes each).</summary>
    public int ChrCount => *At<int>(0x023c44e0);
    public nint ChrArray => *At<nint>(0x023c44e4);
    public const int ChrSize = 0x880;
    /// <summary>A character's fields: <c>u16</c> id, active byte, hide byte, its matrix (local to world), the renderer instance lists, its render cloth (<see cref="ClothHistory"/>).</summary>
    public const int ChrId = 0x0, ChrActive = 0x2, ChrHide = 0x180, ChrMatrix = 0x1d0, ChrInstances = 0x830, ChrCloth = 0x834, ChrShadowInstances = 0x838;
    /// <summary>A character's flags (<c>+0x194</c>); bit 21: its cloth is set up (<c>Ch_UpdateClothMatrix</c>, <c>Sg_ClothPlaceAnchors</c> need it).</summary>
    public const int ChrFlags = 0x194;
    public const uint ChrClothReady = 1u << 21;

    /// <summary>The window (<c>g_hWnd</c>, <c>0x00cc9ce8</c>; Fahrenheit reads it there too).</summary>
    public nint Window => *At<nint>(0x00cc9ce8);

    /// <summary>The renderer's vsync byte: <c>Renderer_PresentFlip</c> (<c>0x00593760</c>) presents with sync interval 1 when <c>renderer + 0x131c</c> is not 0, else 0.</summary>
    public const int RendererVsync = 0x131c;

    // The frame limiter (Game_Main 0x0062ee50): interval = fild [g_FrameVsyncs] * fmul [1/59.94].
    /// <summary><c>fild dword [g_FrameVsyncs]</c> (<c>db 05</c>) at <c>0x0062f2fc</c>: its operand at <c>+2</c>.</summary>
    public const uint LimiterVsyncsOperand = 0x0062f2fe, LimiterVsyncsOpcode = 0x0062f2fc;
    /// <summary><c>fmul qword [0x00b412d8]</c> (<c>dc 0d</c>, 1/59.94 s) at <c>0x0062f318</c>: its operand at <c>+2</c>.</summary>
    public const uint LimiterPerVsyncOperand = 0x0062f31a, LimiterPerVsyncOpcode = 0x0062f318;
    public const uint FrameVsyncsVa = 0x00c30e88, PerVsyncVa = 0x00b412d8;

    public byte CodeByte(uint va) => *At<byte>(va);
    public ReadOnlySpan<byte> Code(uint va, int length) => new(At<byte>(va), length);
    public uint CodeU32(uint va) => *At<uint>(va);
}
