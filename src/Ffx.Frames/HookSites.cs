// SPDX-License-Identifier: MIT

namespace Ffx.Frames;

/// <summary>Where the mod hooks FFX.exe: the functions and call sites, with the bytes it expects there. Pure data, so the tests can check them against the game's file.</summary>
public static class HookSites {
    // Hooked at their call sites (CallHooked): a loop in each branches back into its first 5 bytes.
    public const uint UpdateFfxVa = 0x00822840, UpdateScanVa = 0x00889a80;
    // Hooked at their one call in Sg_MainLoop's effect loop (CallHooked): other callers keep the game's function.
    public const uint Gs1TransferVa = 0x007eb580, FieldParticleVa = 0x009083e0, PartLoopVa = 0x00762240, OtDrawVa = 0x007ec470;

    /// <summary>
    ///     Every function the mod hooks through Fahrenheit: its first bytes, as in the Steam release this was written
    ///     against (<c>??</c>: an absolute address, which a rebased image changes), and its length (to the next function
    ///     in <c>ffx.sym</c>), which <c>FrameHooks.CheckGame</c> scans for branches into the 5 bytes the hook overwrites. A
    ///     different game version fails the check and the mod installs nothing.
    /// </summary>
    public static readonly (uint Va, string Name, string Bytes, int Length)[] Prologues = [
        (0x00820ae0, "Sg_MainLoop", "55 8b ec 81 ec a4 00 00 00", 0xc60),
        (0x00821e80, "Sg_FrameSync", "55 8b ec 83 ec 18 d9 05 ?? ?? ?? ?? d8 45 08", 0x3d0),
        (0x0082d070, "Sg_ControlMain", "6a 00 6a 00 6a 00 e8", 0x110),
        (0x00787e00, "MsEffectProcess", "55 8b ec 53 8b 5d 08 8b c3 56", 0x120),
        (0x007be110, "MsCameraProcess", "55 8b ec 83 ec 44 a1", 0x9d0),
        (0x0083f2e0, "Sg_CameraSetPlayer", "53 8b dc 83 ec 08 83 e4 f0", 0x1d0),
        (0x00833390, "Ch_CalcMiddle", "55 8b ec 83 ec 08 a1", 0x1a0),
        (0x00908320, "yiAnimMain", "68 ?? ?? ?? ?? e8 ?? ?? ?? ?? 59 c3", 0x10),
        (0x00645df0, "graphicVideoUpdate", "e8 ?? ?? ?? ?? 80 b8 d0 06 00 00 00", 0x50),
        (0x007ff680, "op_loop_ffx_after", "53 8b dc 83 ec 08 83 e4 f0", 0xf0),
        (0x0082c0c0, "Sg_DrawFilter", "55 8b ec 83 ec 0c e8", 0x6e0),
        (0x008206c0, "Sg_MainCalcRate", "55 8b ec 51 a1", 0x90),
        (0x0088d3d0, "TkUpdateEventJump", "83 3d ?? ?? ?? ?? 00 74 6d 56 e8", 0x110),
        (0x00639860, "graphicCameraSetWorldMatrix", "55 8b ec 81 ec c0 00 00 00", 0x2d0),
        (0x00593760, "Renderer_PresentFlip", "56 8b f1 8b 56 74 85 d2", 0x30),
        (0x006a52f0, "VfxGeometry_Register", "55 8b ec 53 56 8b 75 08", 0xf0),
        (0x00645840, "graphicVFXSyncPath", "83 3d ?? ?? ?? ?? 00 75 0b", 0x20),
        (0x00656940, "PhyreScene_addVFXDrawEvent", "55 8b ec 56 8b 75 08 57", 0x50),
        (0x00a44b40, "JobSchedule_Ch_Cloth_PerCharacter", "55 8b ec 8b 0d ?? ?? ?? ?? 6a 00", 0x40),
        (0x00830b90, "Ch_DrawClothClear", "55 8b ec 83 ec 10 53 56", 0xe0),
        (0x0082ed50, "Ch_DrawMain", "55 8b ec 81 ec 20 04 00 00", 0x10b0),
        (0x0063c3e0, "graphicCharacterInstanceSetWorldMatrix", "55 8b ec 57 8b 7d 08", 0x50),
        (0x0063c430, "graphicCharacterInstanceSetWorldMatrixByMdlID", "55 8b ec 83 ec 0c 53 8b 5d 08", 0xd0),
        (0x0063b740, "graphicCharacterInstanceSetClothVertexBuffers", "55 8b ec 83 ec 08 8b 0d ?? ?? ?? ?? 53 56 57", 0x90),
        (0x007bed60, "MsCameraResetMove", "55 8b ec 56 57 8b 7d 08", 0xf0),
        (0x007c0790, "MsSetCheckChange", "55 8b ec 8b 55 08 8b 45 0c", 0x180),
    ];

    /// <summary>
    ///     The functions hooked at their call sites instead: each has a loop that branches back to byte 3 (<c>updateFFX</c>
    ///     <c>jne 0x00822843</c> at <c>0x00822876</c>, <c>TkUpdateScanControler</c> <c>jl 0x00889a83</c> at
    ///     <c>0x00889a9b</c>), where MinHook's <c>jmp</c> would be. These are all their callers in FFX.exe (no function
    ///     pointer to them exists): each <c>call rel32</c> is pointed at the mod's entry, which calls the function.
    /// </summary>
    public static readonly (uint Va, string Name, string Bytes, uint[] Calls)[] CallHooked = [
        (UpdateFfxVa, "updateFFX", "55 8b ec 83 3d ?? ?? ?? ?? 00 74 05", [0x0042f5b6]),
        (UpdateScanVa, "TkUpdateScanControler", "56 33 f6 6a 00 56 e8", [0x00820b84, 0x0089ae60]),
        (Gs1TransferVa, "op_gs1_transfer", "53 8b dc 83 ec 08 83 e4 f0", [0x00821477]),
        (FieldParticleVa, "yiCallFieldParticle", "83 3d ?? ?? ?? ?? 00 74 0d", [0x0082148c]),
        (PartLoopVa, "pppPartLoop", "55 8b ec 51 83 3d", [0x008214a2]),
        (OtDrawVa, "op_ot_draw", "6a 00 c7 05", [0x008214ba]),
    ];
}
