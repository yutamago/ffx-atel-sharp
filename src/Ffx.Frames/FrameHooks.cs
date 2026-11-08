// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Fahrenheit;

namespace Ffx.Frames;

/// <summary>
///     The hooks on FFX.exe that carry out the <see cref="FrameDirector"/>'s decisions. Every hook runs on the game
///     thread except <see cref="h_present_flip"/> (the render thread).
/// </summary>
/// <remarks>
///     <para>The frame, from the outside in (docs/research/frame-timing.md, "The loop"):</para>
///     <list type="bullet">
///         <item><c>Game_Main</c>'s limiter: patched to wait the mod's interval (<see cref="ApplyLimiter"/>).</item>
///         <item><c>updateFFX</c> (<c>0x00822840</c>, once per drawn frame; hooked at its call, <see cref="CallHooked"/>): decides the frame; a Repeat frame skips it.</item>
///         <item><c>Sg_MainLoop</c> (<c>0x00820ae0</c>, once per game update): puts the true camera back.</item>
///         <item><c>Sg_FrameSync</c> (<c>0x00821e80</c>): sets the updates (<c>g_VoiceHold</c>) and each update's ticks (exec frames); not run at all on a Redraw frame.</item>
///         <item>The holds (Redraw frames): <c>TkUpdateScanControler</c> (at its two calls), <c>Sg_ControlMain</c>, <c>MsCameraProcess</c>, <c>Sg_CameraSetPlayer</c>, <c>Ch_CalcMiddle</c>, <c>yiAnimMain</c>, <c>graphicVideoUpdate</c>, <c>Sg_DrawFilter</c>, the cloth (<c>Ch_DrawClothClear</c>, <c>JobSchedule_Ch_Cloth_PerCharacter</c>).</item>
///         <item>The effects (<see cref="EffectRecorder"/>): <c>MsEffectProcess</c>, <c>op_loop_ffx_after</c> and, at their calls in <c>Sg_MainLoop</c>, <c>op_gs1_transfer</c>, <c>yiCallFieldParticle</c>, <c>pppPartLoop</c>, <c>op_ot_draw</c>. On a tick their draws (<c>VfxGeometry_Register</c>, <c>graphicVFXSyncPath</c>, <c>PhyreScene_addVFXDrawEvent</c>) are recorded; on a Redraw frame the calls are held and the draws replayed.</item>
///         <item><c>graphicCameraSetWorldMatrix</c> (<c>0x00639860</c>, from <c>Cam_Apply</c> in the draw's <c>MsScreenProcess</c>): records the tick's camera and swaps in the interpolated one.</item>
///         <item>The characters (<see cref="CharacterHistory"/>): recorded after <c>Ch_CalcMiddle</c>; <c>Ch_DrawMain</c> draws them between ticks (bones written into the renderer instances, the world matrices corrected in <c>graphicCharacterInstanceSetWorldMatrix(ByMdlID)</c>).</item>
///         <item>The cloth (<see cref="ClothHistory"/>): recorded at the end of a game update with a tick (the cloth jobs are done); <c>graphicCharacterInstanceSetClothVertexBuffers</c> (<c>0x0063b740</c>, in <c>FFXApplication_render</c>, before the upload) gets the blended vertices and the matrix of the character as drawn.</item>
///         <item><c>Renderer_PresentFlip</c> (<c>0x00593760</c>, render thread): vsync on or off.</item>
///     </list>
///     <para>
///         Before anything goes in, <see cref="CheckGame"/> refuses a function MinHook would break (a branch into its
///         first 5 bytes; <see cref="CodeScan"/>). The first time each hook runs, its chain is checked
///         (<see cref="Chain{T}"/>). Any exception in a hook, or a bad chain, switches the mod to pass-through for the
///         rest of the session, logged once.
///     </para>
/// </remarks>
internal sealed unsafe class FrameHooks {
    // ---- Signatures (all cdecl unless noted). ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int FloatFn(float dt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void MainLoopFn(float dt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int VoidFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int IntFn(int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int PtrFn(nint p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int DoubleFn(double dt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte ByteFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte ByteIntFn(int a);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] internal delegate byte ThisFn(nint self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] internal delegate int ThisIntFn(nint self, int a);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] internal delegate int ThisPtrFn(nint self, nint p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int PtrIntFn(nint p, int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int IntIntFn(int a, int b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int PtrPtrFn(nint p, nint q);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int PtrPtrIntFn(nint p, nint q, int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SegmentBufferFn(nint renderData, int segment, int vertices, int indices);

    private static FhCallHandle<T> At<T>(uint va) where T : Delegate => new(new FhCallLocation("FFX.exe", Game.Offset(va)));

    // Hooked at their call sites (CallHooked): a loop in each branches back into its first 5 bytes.
    private const uint UpdateFfxVa = 0x00822840, UpdateScanVa = 0x00889a80;
    // Hooked at their one call in Sg_MainLoop's effect loop (CallHooked): other callers keep the game's function.
    private const uint Gs1TransferVa = 0x007eb580, FieldParticleVa = 0x009083e0, PartLoopVa = 0x00762240, OtDrawVa = 0x007ec470;
    // Hooked (Fahrenheit, MinHook).
    private static FhCallHandle<MainLoopFn> MainLoop => At<MainLoopFn>(0x00820ae0);
    private static FhCallHandle<FloatFn> FrameSync => At<FloatFn>(0x00821e80);
    private static FhCallHandle<VoidFn> ControlMain => At<VoidFn>(0x0082d070);
    private static FhCallHandle<IntFn> EffectProcess => At<IntFn>(0x00787e00);
    private static FhCallHandle<VoidFn> CameraProcess => At<VoidFn>(0x007be110);
    private static FhCallHandle<VoidFn> CameraSetPlayer => At<VoidFn>(0x0083f2e0);
    private static FhCallHandle<VoidFn> CalcMiddle => At<VoidFn>(0x00833390);
    private static FhCallHandle<VoidFn> YiAnimMain => At<VoidFn>(0x00908320);
    private static FhCallHandle<DoubleFn> VideoUpdate => At<DoubleFn>(0x00645df0);
    private static FhCallHandle<VoidFn> OpLoopAfter => At<VoidFn>(0x007ff680);
    private static FhCallHandle<VoidFn> DrawFilter => At<VoidFn>(0x0082c0c0);
    private static FhCallHandle<VoidFn> MainCalcRate => At<VoidFn>(0x008206c0);
    private static FhCallHandle<VoidFn> EventJump => At<VoidFn>(0x0088d3d0);
    private static FhCallHandle<PtrFn> SetWorldMatrix => At<PtrFn>(0x00639860);
    private static FhCallHandle<ThisFn> PresentFlip => At<ThisFn>(0x00593760);
    private static FhCallHandle<ThisIntFn> VfxRegister => At<ThisIntFn>(0x006a52f0);
    private static FhCallHandle<VoidFn> VfxSyncPath => At<VoidFn>(0x00645840);
    private static FhCallHandle<ThisPtrFn> VfxDrawEvent => At<ThisPtrFn>(0x00656940);
    private static FhCallHandle<PtrIntFn> ClothSchedule => At<PtrIntFn>(0x00a44b40);
    private static FhCallHandle<PtrFn> ClothClear => At<PtrFn>(0x00830b90);
    private static FhCallHandle<VoidFn> DrawMain => At<VoidFn>(0x0082ed50);
    private static FhCallHandle<PtrPtrFn> ChrSetWorld => At<PtrPtrFn>(0x0063c3e0);
    private static FhCallHandle<PtrPtrIntFn> ChrSetWorldByModel => At<PtrPtrIntFn>(0x0063c430);
    private static FhCallHandle<VoidFn> ClothUpload => At<VoidFn>(0x0063b740);
    private static FhCallHandle<IntIntFn> CameraResetMove => At<IntIntFn>(0x007bed60);
    // Called.
    private static FhCallHandle<VoidFn> CanSpeedUp => At<VoidFn>(0x0081fd30);
    private static FhCallHandle<IntFn> SetMuteSound => At<IntFn>(0x00821c60);
    private static FhCallHandle<ByteIntFn> IsMapExcluded => At<ByteIntFn>(0x006f6fa0);
    private static FhCallHandle<ByteFn> IsMagicExcluded => At<ByteFn>(0x006f6ff0);
    private static FhCallHandle<VoidFn> BattleStatus => At<VoidFn>(0x007816f0);
    private static FhCallHandle<VoidFn> SphereGrid => At<VoidFn>(0x00641060);
    private static FhCallHandle<SegmentBufferFn> SegmentBuffer => At<SegmentBufferFn>(0x0063fdb0);

    private readonly Game _game;
    private readonly FhLogger _log;
    private readonly Func<FrameSettings> _settings;
    private readonly FrameDirector _director = new();
    private readonly ScaleGuard _guard;
    private readonly CameraHistory _camera = new();
    private readonly EffectRecorder _effects = new();
    private readonly CharacterHistory _chars = new();
    private readonly DrawCounters _drawCounters = new(Game.DrawCountersSize);
    private readonly ClothHistory _cloth = new();
    public readonly FrameStats Stats = new();

    // The current frame (game thread). Vanilla outside updateFFX, so calls from elsewhere pass through.
    private FrameDecision _frame = new(FrameKind.Vanilla, TickPlan.None, 1, 1, false);
    private bool _inFrame;
    private int _loop;
    private bool _syncFrame;
    private bool _loopTicked;
    private bool _pushedThisLoop;
    private bool _worldSeen, _worldActive = true;
    private bool _muted;
    private bool _wasEnabled;
    private sbyte _execSaved = 1;

    // The camera swapped in (game thread).
    private bool _applied, _settingCamera;
    private Matrix4x4 _trueCam, _trueScr;

    // The limiter's operands (patched to point here).
    private readonly int* _limiterVsyncs;
    private readonly double* _limiterPerVsync;
    private bool _limiterPatched;

    private readonly byte* _fades;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _nextMonitorCheck;

    private volatile int _vsyncWanted = -1; // -1 the game's, 0 off, 1 on
    private int _failed;

    // Live values for the window (written on the game thread, read on the render thread).
    public volatile int MonitorHz;
    public volatile string? ExcludedReason;
    public volatile float LastAlpha;
    public volatile float LastScale = 1;
    public volatile int LastTicks;
    /// <summary>The tick rate of the last frame (30 in the world, 60 in the main menu).</summary>
    public volatile float LastRate = 30;
    /// <summary>The most ticks the frame's time budget allows now (<see cref="TickBudget"/>).</summary>
    public volatile int BudgetTicks;
    /// <summary>The measured cost of one tick, milliseconds.</summary>
    public volatile float TickMs;
    private double _droppedCarry;
    public volatile string Status = "starting";
    public bool Failed => Volatile.Read(ref _failed) != 0;
    public CameraHistory Camera => _camera;
    /// <summary>For the window (read on the render thread; approximate).</summary>
    public int EffectDraws => _effects.Count;
    public int Characters => _chars.Characters;
    public int CharacterCuts => _chars.Cuts;
    public int ClothMeshesHeld => _cloth.Meshes;
    public bool LimiterPatched => _limiterPatched;
    /// <summary>The start-up ramp and the scale that may be saved (<see cref="ScaleGuard"/>).</summary>
    public ScaleGuard Guard => _guard;

    // The call-site hooks' entry points (kept alive: the game calls them) and the functions they call.
    private readonly FloatFn _updateEntry;
    private readonly VoidFn _updateScanEntry, _gs1Entry, _fieldParticleEntry, _partLoopEntry, _otDrawEntry;
    private FloatFn? _nextUpdate;
    private VoidFn? _nextUpdateScan, _nextGs1, _nextFieldParticle, _nextPartLoop, _nextOtDraw;

    // Effects (EffectRecorder): replaying, the geometries the render data table holds now, those copied this update.
    private ThisIntFn? _nextRegister;
    private VoidFn? _nextSyncPath;
    private ThisPtrFn? _nextDrawEvent;
    private SegmentBufferFn? _segmentBuffer;
    private bool _replaying;
    private int _gameThread = -1;
    private int _updateSerial, _liveSerial = -1;
    private readonly HashSet<nint> _live = [], _copied = [];
    private readonly int* _event;

    // Characters (CharacterHistory): the world-space correction per instance list during Ch_DrawMain, the bone arrays written.
    private PtrIntFn? _nextClothSchedule;
    private PtrFn? _nextClothClear;
    private VoidFn? _nextDrawMain;
    private PtrPtrFn? _nextChrSetWorld;
    private PtrPtrIntFn? _nextChrSetWorldByModel;
    private bool _inDrawMain;
    private int _charMap = -1;
    private readonly Dictionary<nint, Matrix4x4> _rootDelta = [];
    // The bones the mod wrote into each instance between ticks (what the array held when it was last left).
    private readonly Dictionary<nint, Matrix4x4[]> _bonesWritten = [];
    private readonly HashSet<nint> _bonesSeen = [];
    private readonly Matrix4x4* _world;
    private Matrix4x4[] _boneScratch = new Matrix4x4[256];
    // The cloth (ClothHistory): each character's matrix as drawn (Ch_DrawMain), what the mod wrote into each cloth mesh,
    // whether this update recorded the characters (then the cloth is recorded at its end), whether the upload is armed.
    private VoidFn? _nextClothUpload;
    private readonly Dictionary<nint, Matrix4x4> _drawnRoot = [];
    private readonly Dictionary<nint, (Matrix4x4 World, Vector4[] Vertices)> _clothWritten = [];
    private readonly HashSet<nint> _clothSeen = [];
    private Vector4[] _clothScratch = new Vector4[1024];
    private bool _clothRecord, _clothArmed, _clothBlend, _clothBones;
    // A camera cut: the game set its camera at once in this update (MsCameraResetMove); the world matrices Ch_DrawMain
    // handed the renderer corrected this update (to send the true ones again when the camera turns out to have cut).
    private IntIntFn? _nextCameraResetMove;
    private bool _cameraSnapped;
    private readonly WorldMatrixLog _worldCalls = new();
    private readonly List<WorldMatrixLog.Entry> _worldReplay = [];

    private FloatFn? _nextFrameSync;
    private MainLoopFn? _nextMainLoop;
    private VoidFn? _nextControlMain, _nextCameraProcess, _nextCameraSetPlayer, _nextCalcMiddle, _nextYi, _nextOpLoopAfter, _nextDrawFilter, _nextCalcRate, _nextEventJump;
    private IntFn? _nextEffect;
    private DoubleFn? _nextVideo;
    private PtrFn? _nextSetWorld;
    private ThisFn? _nextPresent;
    private VoidFn? _canSpeedUp, _battleStatus, _sphereGrid;
    private IntFn? _setMute;
    private ByteIntFn? _mapExcluded;
    private ByteFn? _magicExcluded;

    public FrameHooks(Game game, FhLogger log, Func<FrameSettings> settings) {
        _game = game;
        _log = log;
        _settings = settings;
        _guard = new ScaleGuard(settings().TimeScale);
        _limiterVsyncs = (int*)NativeMemory.AllocZeroed(16);
        _limiterPerVsync = (double*)NativeMemory.AllocZeroed(16);
        *_limiterVsyncs = 2;
        *_limiterPerVsync = FramePacing.GameVsync;
        _fades = (byte*)NativeMemory.AllocZeroed(Game.FadeStateSize);
        _event = (int*)NativeMemory.AlignedAlloc(16, 16);
        _world = (Matrix4x4*)NativeMemory.AlignedAlloc((nuint)sizeof(Matrix4x4), 16);
        _updateEntry = h_update_ffx;
        _updateScanEntry = h_update_scan;
        _gs1Entry = h_gs1_transfer;
        _fieldParticleEntry = h_field_particle;
        _partLoopEntry = h_part_loop;
        _otDrawEntry = h_ot_draw;
    }

    /// <summary>
    ///     Every function the mod hooks through Fahrenheit: its first bytes, as in the Steam release this was written
    ///     against (<c>??</c>: an absolute address, which a rebased image changes), and its length (to the next function
    ///     in <c>ffx.sym</c>), which <see cref="CheckGame"/> scans for branches into the 5 bytes the hook overwrites. A
    ///     different game version fails the check and the mod installs nothing.
    /// </summary>
    private static readonly (uint Va, string Name, string Bytes, int Length)[] Prologues = [
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
    ];

    /// <summary>
    ///     The functions hooked at their call sites instead: each has a loop that branches back to byte 3 (<c>updateFFX</c>
    ///     <c>jne 0x00822843</c> at <c>0x00822876</c>, <c>TkUpdateScanControler</c> <c>jl 0x00889a83</c> at
    ///     <c>0x00889a9b</c>), where MinHook's <c>jmp</c> would be. These are all their callers in FFX.exe (no function
    ///     pointer to them exists): each <c>call rel32</c> is pointed at the mod's entry, which calls the function.
    /// </summary>
    private static readonly (uint Va, string Name, string Bytes, uint[] Calls)[] CallHooked = [
        (UpdateFfxVa, "updateFFX", "55 8b ec 83 3d ?? ?? ?? ?? 00 74 05", [0x0042f5b6]),
        (UpdateScanVa, "TkUpdateScanControler", "56 33 f6 6a 00 56 e8", [0x00820b84, 0x0089ae60]),
        (Gs1TransferVa, "op_gs1_transfer", "53 8b dc 83 ec 08 83 e4 f0", [0x00821477]),
        (FieldParticleVa, "yiCallFieldParticle", "83 3d ?? ?? ?? ?? 00 74 0d", [0x0082148c]),
        (PartLoopVa, "pppPartLoop", "55 8b ec 51 83 3d", [0x008214a2]),
        (OtDrawVa, "op_ot_draw", "6a 00 c7 05", [0x008214ba]),
    ];

    private bool Matches(uint va, string name, string bytes) {
        string[] want = bytes.Split(' ');
        for (int i = 0; i < want.Length; i++) {
            if (want[i] == "??" || _game.CodeByte(va + (uint)i) == Convert.ToByte(want[i], 16)) continue;
            _log.Error($"Frames: {name} (0x{va:x8}) is not the code this mod knows (byte {i}); another game version?");
            return false;
        }
        return true;
    }

    /// <summary>
    ///     Whether every hook can go in safely: each function starts as expected, none hooked through Fahrenheit has a
    ///     branch into its first 5 bytes, and each call site is a <c>call</c> to its function. Logs each problem. Nothing
    ///     is installed unless all pass.
    /// </summary>
    public bool CheckGame() {
        bool ok = true;
        foreach ((uint va, string name, string bytes, int length) in Prologues) {
            if (!Matches(va, name, bytes)) {
                ok = false;
                continue;
            }
            int at = CodeScan.PrologueBranch(_game.Code(va, length), va);
            if (at < 0) continue;
            _log.Error($"Frames: {name} (0x{va:x8}) cannot be hooked: the code at 0x{va + (uint)at:x8} branches into its first {CodeScan.PatchSize} bytes, which the hook overwrites.");
            ok = false;
        }
        foreach ((uint va, string name, string bytes, uint[] calls) in CallHooked) {
            if (!Matches(va, name, bytes)) {
                ok = false;
                continue;
            }
            foreach (uint site in calls) {
                if (CodeScan.TryCallTarget(_game.Code(site, 5), site, out uint target) && target == va) continue;
                _log.Error($"Frames: 0x{site:x8} is not the call to {name} this mod knows (another game version, or another mod patched it).");
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>
    ///     Points <see cref="CallHooked"/>'s call sites at the mod and queues the other hooks (in <c>init</c>: Fahrenheit
    ///     applies them after all modules are initialised). False and a log line for each that failed.
    /// </summary>
    public bool Install(FhModule owner) {
        bool ok = true;
        void Hook(bool done, string name) {
            if (done) return;
            ok = false;
            _log.Error($"Frames: cannot hook {name}");
        }
        _nextUpdate = Marshal.GetDelegateForFunctionPointer<FloatFn>(_game.Address(UpdateFfxVa));
        _nextUpdateScan = Marshal.GetDelegateForFunctionPointer<VoidFn>(_game.Address(UpdateScanVa));
        Hook(PatchCalls(UpdateFfxVa, Marshal.GetFunctionPointerForDelegate(_updateEntry)), "updateFFX");
        Hook(PatchCalls(UpdateScanVa, Marshal.GetFunctionPointerForDelegate(_updateScanEntry)), "TkUpdateScanControler");
        _nextGs1 = Marshal.GetDelegateForFunctionPointer<VoidFn>(_game.Address(Gs1TransferVa));
        _nextFieldParticle = Marshal.GetDelegateForFunctionPointer<VoidFn>(_game.Address(FieldParticleVa));
        _nextPartLoop = Marshal.GetDelegateForFunctionPointer<VoidFn>(_game.Address(PartLoopVa));
        _nextOtDraw = Marshal.GetDelegateForFunctionPointer<VoidFn>(_game.Address(OtDrawVa));
        Hook(PatchCalls(Gs1TransferVa, Marshal.GetFunctionPointerForDelegate(_gs1Entry)), "op_gs1_transfer");
        Hook(PatchCalls(FieldParticleVa, Marshal.GetFunctionPointerForDelegate(_fieldParticleEntry)), "yiCallFieldParticle");
        Hook(PatchCalls(PartLoopVa, Marshal.GetFunctionPointerForDelegate(_partLoopEntry)), "pppPartLoop");
        Hook(PatchCalls(OtDrawVa, Marshal.GetFunctionPointerForDelegate(_otDrawEntry)), "op_ot_draw");
        Hook(MainLoop.hook(owner, h_main_loop), "Sg_MainLoop");
        Hook(FrameSync.hook(owner, h_frame_sync), "Sg_FrameSync");
        Hook(ControlMain.hook(owner, h_control_main), "Sg_ControlMain");
        Hook(EffectProcess.hook(owner, h_effect_process), "MsEffectProcess");
        Hook(CameraProcess.hook(owner, h_camera_process), "MsCameraProcess");
        Hook(CameraSetPlayer.hook(owner, h_camera_set_player), "Sg_CameraSetPlayer");
        Hook(CalcMiddle.hook(owner, h_calc_middle), "Ch_CalcMiddle");
        Hook(YiAnimMain.hook(owner, h_yi_anim), "yiAnimMain");
        Hook(VideoUpdate.hook(owner, h_video_update), "graphicVideoUpdate");
        Hook(OpLoopAfter.hook(owner, h_op_loop_after), "op_loop_ffx_after");
        Hook(DrawFilter.hook(owner, h_draw_filter), "Sg_DrawFilter");
        Hook(MainCalcRate.hook(owner, h_calc_rate), "Sg_MainCalcRate");
        Hook(EventJump.hook(owner, h_event_jump), "TkUpdateEventJump");
        Hook(SetWorldMatrix.hook(owner, h_set_world_matrix), "graphicCameraSetWorldMatrix");
        Hook(PresentFlip.hook(owner, h_present_flip), "Renderer_PresentFlip");
        Hook(VfxRegister.hook(owner, h_vfx_register), "VfxGeometry_Register");
        Hook(VfxSyncPath.hook(owner, h_vfx_sync_path), "graphicVFXSyncPath");
        Hook(VfxDrawEvent.hook(owner, h_vfx_draw_event), "PhyreScene_addVFXDrawEvent");
        Hook(ClothSchedule.hook(owner, h_cloth_schedule), "JobSchedule_Ch_Cloth_PerCharacter");
        Hook(ClothClear.hook(owner, h_cloth_clear), "Ch_DrawClothClear");
        Hook(DrawMain.hook(owner, h_draw_main), "Ch_DrawMain");
        Hook(ChrSetWorld.hook(owner, h_chr_set_world), "graphicCharacterInstanceSetWorldMatrix");
        Hook(ChrSetWorldByModel.hook(owner, h_chr_set_world_by_model), "graphicCharacterInstanceSetWorldMatrixByMdlID");
        Hook(ClothUpload.hook(owner, h_cloth_upload), "graphicCharacterInstanceSetClothVertexBuffers");
        Hook(CameraResetMove.hook(owner, h_camera_reset_move), "MsCameraResetMove");
        return ok;
    }

    /// <summary>Points every call to <paramref name="va"/> in <see cref="CallHooked"/> at <paramref name="entry"/>.</summary>
    private bool PatchCalls(uint va, nint entry) {
        bool ok = true;
        foreach ((uint fn, _, _, uint[] calls) in CallHooked) {
            if (fn != va) continue;
            foreach (uint site in calls) {
                nint at = _game.Address(site);
                ok &= Win32.PatchU32(at + 1, CodeScan.CallRel32((uint)at, (uint)entry));
            }
        }
        return ok;
    }

    /// <summary>
    ///     The next link of a Fahrenheit hook's chain, checked (<see cref="CodeScan.ChainProblem"/>) the first time the
    ///     hook runs, when Fahrenheit has applied every hook. A problem switches the mod to pass-through (logged once) and
    ///     returns null: that hook then skips its function, which a broken link could not run without crashing the game.
    /// </summary>
    private T? Chain<T>(FhCallHandle<T> handle, T hook, uint va) where T : Delegate {
        T? next = handle.chain_from(hook).fnptr;
        nint at = next is null ? 0 : Marshal.GetFunctionPointerForDelegate(next);
        nint original = _game.Address(va);
        (string name, byte first) = ("0x" + va.ToString("x8"), _game.CodeByte(va));
        foreach ((uint fn, string n, string bytes, _) in Prologues) {
            if (fn == va) (name, first) = (n, Convert.ToByte(bytes[..2], 16));
        }
        string? problem = CodeScan.ChainProblem(at, original, _game.CodeByte(va), at == 0 ? (byte)0 : *(byte*)at, first);
        if (problem is null) return next;
        Fail($"the hook on {name}", problem);
        return null;
    }

    /// <summary>
    ///     Points the limiter's <c>fild</c> and <c>fmul</c> at the mod's two values (<see cref="FramePacing.Limiter"/>),
    ///     after checking that the code is the game's (<c>db 05</c> / <c>dc 0d</c> with the expected operands).
    /// </summary>
    public bool PatchLimiter() {
        uint wantVsyncs = (uint)_game.Address(Game.FrameVsyncsVa), wantPerVsync = (uint)_game.Address(Game.PerVsyncVa);
        if (_game.CodeByte(Game.LimiterVsyncsOpcode) != 0xdb || _game.CodeByte(Game.LimiterVsyncsOpcode + 1) != 0x05
            || _game.CodeU32(Game.LimiterVsyncsOperand) != wantVsyncs
            || _game.CodeByte(Game.LimiterPerVsyncOpcode) != 0xdc || _game.CodeByte(Game.LimiterPerVsyncOpcode + 1) != 0x0d
            || _game.CodeU32(Game.LimiterPerVsyncOperand) != wantPerVsync) {
            _log.Error("Frames: the frame limiter's code is not what this mod knows (another game version, or another mod patched it); the frame rate stays the game's.");
            return false;
        }
        *_limiterVsyncs = _game.FrameVsyncs;
        _limiterPatched = Win32.PatchU32(_game.Address(Game.LimiterVsyncsOperand), (uint)(nint)_limiterVsyncs)
            && Win32.PatchU32(_game.Address(Game.LimiterPerVsyncOperand), (uint)(nint)_limiterPerVsync);
        if (!_limiterPatched) _log.Error("Frames: cannot patch the frame limiter (VirtualProtect failed).");
        return _limiterPatched;
    }

    private void Fail(string where, Exception ex) => Fail(where, ex.ToString());

    private void Fail(string where, string why) {
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        _log.Error($"Frames: {where} failed; the mod passes everything through to the game from now on: {why}");
        Status = $"stopped: {where} failed (see the log)";
        try {
            *_limiterVsyncs = _game.FrameVsyncs;
            *_limiterPerVsync = FramePacing.GameVsync;
            _vsyncWanted = -1;
            if (_applied) RestoreCamera();
        }
        catch {
            // Nothing more to do.
        }
    }

    private bool Redraw => _inFrame && _frame.Kind == FrameKind.Redraw;

    // ------------------------------------------------------------------ the frame

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_update_ffx(float dt) {
        FloatFn next = _nextUpdate!;
        if (Failed) return next(dt);
        _gameThread = Environment.CurrentManagedThreadId;
        FrameSettings s = _settings();
        try {
            BeginFrame(dt, s);
        }
        catch (Exception ex) {
            Fail("the frame decision", ex);
            _inFrame = false;
            return next(dt);
        }
        Stats.Add(Counter.Frames);
        if (_frame.Kind == FrameKind.Repeat) {
            Stats.Add(Counter.RepeatFrames);
            try {
                if (s.Interpolate && s.InterpolateRepeats && _frame.Alpha < 1 && !_game.MovieOn && !_game.WaitFramesByTime) MoveCameraWithoutUpdate(_frame.Alpha);
            }
            catch (Exception ex) {
                Fail("the camera on a repeated frame", ex);
            }
            _inFrame = false;
            return 0;
        }
        if (_frame.Kind == FrameKind.Redraw) Stats.Add(Counter.RedrawFrames);
        else if (_frame.Kind == FrameKind.Tick) Stats.Add(Counter.TickFrames);
        _loop = 0;
        _syncFrame = false;
        _worldSeen = false;
        _inFrame = true;
        long start = Stopwatch.GetTimestamp();
        try {
            return next(dt);
        }
        finally {
            _inFrame = false;
            if (_frame.Kind != FrameKind.Vanilla) {
                _game.ExecFrames = _execSaved;
                _worldActive = _worldSeen;
            }
            // The time budget learns a tick's cost from the world's frames (a menu, a pause or a load runs no world tick).
            if (_frame.Kind == FrameKind.Tick && _worldSeen && !_syncFrame && !Failed) {
                _director.Observe(Stopwatch.GetElapsedTime(start).TotalSeconds, _frame.Plan.Ticks, s);
            }
        }
    }

    /// <summary>Reads the game, asks the director, and sets up what the frame needs before the game's update runs.</summary>
    private void BeginFrame(float dt, FrameSettings s) {
        double now = _clock.Elapsed.TotalSeconds;
        if (now >= _nextMonitorCheck) {
            _nextMonitorCheck = now + 1;
            MonitorHz = Win32.RefreshRate(_game.Window);
        }
        if (s.Enabled != _wasEnabled) {
            _wasEnabled = s.Enabled;
            _camera.Clear();
            _effects.Clear();
            _game.ClearCarries();
            _log.Info($"Frames: {(s.Enabled ? "on" : "off")}");
        }
        ApplyLimiter(s);
        _vsyncWanted = !s.Enabled ? -1 : s.Vsync switch { VsyncMode.On => 1, VsyncMode.Off => 0, _ => -1 };

        int vsyncs = _game.FrameVsyncs;
        bool movie = _game.MovieOn || _game.WaitFramesByTime;
        // At start-up the scale ramps up from ×1 (a saved fast-forward must not hit the first world frame at once); the
        // game's checks are not called while it runs at ×1.
        double scale = _guard.Effective(s.TimeScale);
        string? excluded = null;
        if (s.Enabled && Math.Abs(scale - 1) > 1e-9) excluded = Excluded(s);
        ExcludedReason = excluded;
        FrameInput input = new(dt, vsyncs, _worldActive, movie, excluded is not null, s.Enabled ? _game.Booster : 1, scale);
        _frame = _director.Next(input, s);
        if (_frame.Kind is FrameKind.Tick or FrameKind.Redraw && _worldActive && !movie) _guard.WorldFrame(dt, excluded is null ? scale : 1);
        _execSaved = _game.ExecFrames;
        if (_execSaved < 1) _execSaved = 1;
        LastAlpha = (float)_frame.Alpha;
        LastScale = (float)_frame.Scale;
        LastTicks = _frame.Plan.Ticks;
        LastRate = (float)_director.Accumulator.Rate;
        BudgetTicks = _director.MaxTicks(s);
        TickMs = (float)(_director.Budget.SecondsPerTick * 1000);
        Stats.Add(Counter.PlannedTicks, _frame.Plan.Ticks);
        _droppedCarry += _frame.Dropped;
        if (_droppedCarry >= 1) {
            long whole = (long)Math.Floor(_droppedCarry);
            Stats.Add(Counter.DroppedTicks, whole);
            _droppedCarry -= whole;
        }
        if (_frame.Kind == FrameKind.Vanilla && _applied) RestoreCamera();
        if (_frame.Kind == FrameKind.Vanilla) {
            _camera.Clear();
            _effects.Clear();
            _drawCounters.Clear();
        }
        ApplyMute(_frame.Kind != FrameKind.Vanilla && _frame.Mute);
        Status = _frame.Kind == FrameKind.Vanilla ? "off: the game's own timing" : "running";
    }

    /// <summary>Why the time scale stays ×1 now, or null. Calls into the game (<c>Sg_CanSpeedUp</c> and the booster's checks), so only when the scale is not 1.</summary>
    private string? Excluded(FrameSettings s) {
        _canSpeedUp ??= CanSpeedUp.fnptr!;
        bool booster = false;
        if (s.RespectBoosterLimits && _game.BoosterReady) {
            _mapExcluded ??= IsMapExcluded.fnptr!;
            _magicExcluded ??= IsMagicExcluded.fnptr!;
            _battleStatus ??= BattleStatus.fnptr!;
            _sphereGrid ??= SphereGrid.fnptr!;
            booster = _mapExcluded(_game.Map) != 0
                || (_battleStatus() == 1 && _magicExcluded() != 0)
                || _sphereGrid() != 0
                || _game.SaveBusy;
        }
        return TimeScaleRules.Excluded(_game.SyncOn, _game.WaitFramesByTime, _game.MovieFading, _canSpeedUp() != 0, booster);
    }

    private void ApplyLimiter(FrameSettings s) {
        if (!_limiterPatched) return;
        double? interval = s.Enabled ? FramePacing.Interval(s.Target, s.FixedFps, MonitorHz) : null;
        (int vsyncs, double perVsync) = FramePacing.Limiter(interval, _game.FrameVsyncs);
        *_limiterVsyncs = vsyncs;
        *_limiterPerVsync = perVsync;
    }

    private void ApplyMute(bool mute) {
        if (mute == _muted) return;
        _setMute ??= SetMuteSound.fnptr!;
        // The booster mutes on its own; leave the sound off while it runs.
        if (mute || !_game.BoosterOn) _setMute(mute ? 1 : 0);
        _muted = mute;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_main_loop(float dt) {
        MainLoopFn? next = _nextMainLoop ??= Chain(MainLoop, h_main_loop, 0x00820ae0);
        if (next is null) return;
        if (Failed) {
            next(dt);
            return;
        }
        bool redraw = Redraw;
        int calm = 0;
        Span<byte> counters = stackalloc byte[Game.DrawCountersSize];
        try {
            if (_applied) RestoreCamera();
            _pushedThisLoop = false;
            _loopTicked = false;
            _clothRecord = false;
            _cameraSnapped = false;
            _updateSerial++;
            _worldCalls.BeginUpdate(_updateSerial);
            if (redraw) {
                calm = _game.CalmFrames;
                // The draw's own counters (the title's water, the dream noise): drawn as at the last tick, then put back.
                _game.ReadDrawCounters(counters);
                _drawCounters.BeginRedraw(counters);
                _game.WriteDrawCounters(counters);
            }
            else if (_inFrame && _frame.Kind == FrameKind.Tick) {
                // A game update with ticks records the effects' draws afresh: a call it does not reach replays nothing.
                _effects.Clear();
                _game.ReadDrawCounters(counters);
                _drawCounters.BeginTick(counters);
            }
        }
        catch (Exception ex) {
            Fail("Sg_MainLoop", ex);
        }
        Stats.Add(Counter.Updates);
        next(dt);
        if (redraw && !Failed) {
            _game.CalmFrames = calm;
            _drawCounters.EndRedraw(counters);
            _game.WriteDrawCounters(counters);
        }
        if (_clothRecord && !Failed) {
            // Sg_MainLoop waited for the cloth jobs Ch_DrawMain queued: the tick's cloth is final.
            _clothRecord = false;
            try {
                RecordCloth();
            }
            catch (Exception ex) {
                Fail("the cloth's history", ex);
            }
        }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_frame_sync(float dt) {
        FloatFn? next = _nextFrameSync ??= Chain(FrameSync, h_frame_sync, 0x00821e80);
        if (next is null) return 0;
        if (Failed || !_inFrame || _frame.Kind is FrameKind.Vanilla or FrameKind.Repeat) return next(dt);
        try {
            if (_frame.Kind == FrameKind.Redraw) {
                // Nothing of Sg_FrameSync runs: no pad scan, no v-blank count, no sync clock, no effect read poll.
                _game.ExecFrames = 0;
                _loopTicked = false;
                return 0;
            }
            if (_game.VoiceHold == 0) {
                // The frame's first update: let the game run its own step (the sync clock starts and steps there), then
                // replace its updates with the plan unless a voice-synced scene paces them.
                bool syncBefore = _game.SyncOn;
                int r = next(dt);
                if (syncBefore || _game.SyncOn) {
                    _syncFrame = true;
                    _game.ExecFrames = 1;
                }
                else {
                    _game.ClearCarries();
                    _game.VoiceHold = Math.Max(1, _frame.Plan.Loops);
                    _game.ExecFrames = (sbyte)Math.Max(1, _frame.Plan.ExecFor(0));
                }
                _loop = 1;
                _loopTicked = true;
                return r;
            }
            int rest = next(dt);
            _game.ExecFrames = (sbyte)(_syncFrame ? 1 : Math.Max(1, _frame.Plan.ExecFor(_loop)));
            _loop++;
            _loopTicked = true;
            return rest;
        }
        catch (Exception ex) {
            Fail("Sg_FrameSync", ex);
            _game.ExecFrames = _execSaved;
            return 0;
        }
    }

    // ------------------------------------------------------------------ the holds (Redraw frames)

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_update_scan() {
        return Redraw && !Failed ? 0 : _nextUpdateScan!();
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_control_main() {
        VoidFn? next = _nextControlMain ??= Chain(ControlMain, h_control_main, 0x0082d070);
        if (next is null) return 0;
        return Redraw && !Failed ? 0 : next();
    }

    // ------------------------------------------------------------------ the effects (Redraw frames: held, their draws replayed)

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_effect_process(int half) {
        IntFn? next = _nextEffect ??= Chain(EffectProcess, h_effect_process, 0x00787e00);
        if (next is null) return 0;
        // The update half is held on every Redraw frame, as before; only its draws (if any) are replayed.
        EffectSlot slot = half == 0 ? EffectSlot.Update : EffectSlot.EffectsAfter;
        EffectsMode mode = half == 0 && _settings().Effects == EffectsMode.Run ? EffectsMode.Replay : _settings().Effects;
        switch (EffectNow(mode)) {
            case EffectAction.Hold:
                return 0;
            case EffectAction.Replay:
                ReplayEffects(slot);
                return 0;
        }
        if (!Recording(mode)) return next(half);
        _effects.Begin(slot);
        try {
            return next(half);
        }
        finally {
            _effects.End();
        }
    }

    /// <summary>The start of each pass of the draw's effect loop: the effects' per-pass counters, the pads they read, the random latches.</summary>
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_op_loop_after() {
        VoidFn? next = _nextOpLoopAfter ??= Chain(OpLoopAfter, h_op_loop_after, 0x007ff680);
        if (next is null) return 0;
        EffectsMode mode = _settings().Effects;
        if (EffectNow(mode) != EffectAction.Run) return 0;
        if (Recording(mode)) _effects.BeginDrawPass();
        return next();
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_gs1_transfer() => Effect(EffectSlot.Gs1Transfer, _nextGs1!);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_field_particle() => Effect(EffectSlot.FieldParticles, _nextFieldParticle!);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_part_loop() => Effect(EffectSlot.PartLoop, _nextPartLoop!);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_ot_draw() => Effect(EffectSlot.OtDraw, _nextOtDraw!);

    /// <summary>What an effect call does now (<see cref="EffectGate"/>): always Run outside a Tick or Redraw frame of the mod.</summary>
    private EffectAction EffectNow(EffectsMode mode) {
        if (Failed || !_inFrame || _frame.Kind is FrameKind.Vanilla or FrameKind.Repeat) return EffectAction.Run;
        return EffectGate.Decide(mode, _frame.Kind == FrameKind.Redraw);
    }

    /// <summary>Whether this call's draws are recorded: a frame with ticks, with replay on.</summary>
    private bool Recording(EffectsMode mode) => mode == EffectsMode.Replay && !Failed && _inFrame && _frame.Kind == FrameKind.Tick;

    /// <summary>Runs, holds or replays one of the draw loop's effect calls (hooked at its call in <c>Sg_MainLoop</c>).</summary>
    private int Effect(EffectSlot slot, VoidFn next) {
        EffectsMode mode = _settings().Effects;
        switch (EffectNow(mode)) {
            case EffectAction.Hold:
                return 0;
            case EffectAction.Replay:
                ReplayEffects(slot);
                return 0;
        }
        if (!Recording(mode)) return next();
        _effects.Begin(slot);
        try {
            return next();
        }
        finally {
            _effects.End();
        }
    }

    /// <summary>Whether a draw handed to the renderer now belongs to the effect call being recorded (game thread only: the job workers draw too).</summary>
    private bool Records => _effects.Recording is not null && !_replaying && !Failed && Environment.CurrentManagedThreadId == _gameThread;

    /// <summary>
    ///     <c>VfxGeometry_Register</c> (<c>VFXDynamicGeometry</c> vtable <c>+8</c>, thiscall): updates the geometry's render
    ///     data for the renderer and puts it on the scene's VFX list for this frame. Every effect draw goes through it.
    /// </summary>
    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private int h_vfx_register(nint geometry, int flags) {
        ThisIntFn? next = _nextRegister ??= Chain(VfxRegister, h_vfx_register, 0x006a52f0);
        if (next is null) return 0;
        if (Records) _effects.Add(new EffectDraw(EffectDrawKind.Register, geometry, flags, _game.VfxCurrentType));
        return next(geometry, flags);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_vfx_sync_path() {
        VoidFn? next = _nextSyncPath ??= Chain(VfxSyncPath, h_vfx_sync_path, 0x00645840);
        if (next is null) return 0;
        if (Records) _effects.Add(new EffectDraw(EffectDrawKind.SyncPath));
        return next();
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private int h_vfx_draw_event(nint scene, nint ev) {
        ThisPtrFn? next = _nextDrawEvent ??= Chain(VfxDrawEvent, h_vfx_draw_event, 0x00656940);
        if (next is null) return 0;
        if (Records && ev != 0) {
            int* e = (int*)ev;
            _effects.Add(new EffectDraw(EffectDrawKind.DrawEvent, scene, E0: e[0], E1: e[1], E2: e[2], E3: e[3]));
        }
        return next(scene, ev);
    }

    /// <summary>
    ///     Hands the renderer again what <paramref name="slot"/>'s call handed it at the last tick: each geometry still in
    ///     the render data table, its vertex arrays copied into this frame's dynamic-geometry buffer, put on the VFX list
    ///     with the same flags; the paths and draw events in between, in order.
    /// </summary>
    private void ReplayEffects(EffectSlot slot) {
        IReadOnlyList<EffectDraw> draws = _effects.Entries(slot);
        if (draws.Count == 0 || _game.GraphicSkipFrame) return;
        try {
            if (_liveSerial != _updateSerial) {
                _liveSerial = _updateSerial;
                LiveGeometries();
                _copied.Clear();
            }
            _replaying = true;
            byte type = _game.VfxCurrentType;
            try {
                foreach (EffectDraw d in draws) {
                    switch (d.Kind) {
                        case EffectDrawKind.Register:
                            if (_nextRegister is null) break;
                            if (!_live.Contains(d.Target)) {
                                Stats.Add(Counter.EffectsGone);
                                break;
                            }
                            if (_copied.Add(d.Target)) CopyVertices(d.Target);
                            _game.VfxCurrentType = d.VfxType;
                            _nextRegister(d.Target, d.Flags);
                            Stats.Add(Counter.EffectReplays);
                            break;
                        case EffectDrawKind.SyncPath:
                            _nextSyncPath?.Invoke();
                            break;
                        case EffectDrawKind.DrawEvent:
                            if (_nextDrawEvent is null || d.Target == 0 || d.Target != _game.PhyreScene) break;
                            _event[0] = d.E0;
                            _event[1] = d.E1;
                            _event[2] = d.E2;
                            _event[3] = d.E3;
                            _nextDrawEvent(d.Target, (nint)_event);
                            break;
                    }
                }
            }
            finally {
                _game.VfxCurrentType = type;
                _replaying = false;
            }
        }
        catch (Exception ex) {
            Fail("the effects' replay", ex);
        }
    }

    /// <summary>The geometries the VFX render data table holds now (its <c>std::map</c> walked in order, as <c>ClearVFXDrawData</c> does).</summary>
    private void LiveGeometries() {
        _live.Clear();
        nint table = _game.VfxRenderDataTable;
        if (table == 0) return;
        nint head = *(nint*)table;
        if (head == 0) return;
        nint node = *(nint*)head;
        for (int guard = 0; node != head && node != 0 && guard < 1 << 20; guard++) {
            if (*(byte*)(node + 0xd) != 0) break;
            nint geometry = *(nint*)(node + 0x18);
            if (geometry != 0) _live.Add(geometry);
            nint right = *(nint*)(node + 8);
            if (right == 0) break;
            if (*(byte*)(right + 0xd) == 0) {
                node = right;
                for (nint left = *(nint*)node; *(byte*)(left + 0xd) == 0; left = *(nint*)node) node = left;
            }
            else {
                nint parent = *(nint*)(node + 4);
                while (*(byte*)(parent + 0xd) == 0 && node == *(nint*)(parent + 8)) {
                    node = parent;
                    parent = *(nint*)(node + 4);
                }
                node = parent;
            }
        }
    }

    /// <summary>
    ///     The geometry's vertex arrays of the frame before, copied into this frame's dynamic-geometry buffer: the buffers
    ///     flip every drawn frame and the flip clears each segment's entry for the new buffer (<c>ClearVFXDrawData</c>), so a
    ///     geometry no effect wrote this frame has nothing to draw. Each segment with data in the previous buffer and none
    ///     in the current one gets a block (<c>graphicDynGeoMemManagerGetBufferForSegment</c>, which also records it as the
    ///     current one) and its five arrays copied.
    /// </summary>
    private void CopyVertices(nint geometry) {
        nint rd = *(nint*)(geometry + 0x24);
        if (rd == 0 || !_game.TryDynGeoBuffers(out int cur, out int count)) return;
        _segmentBuffer ??= SegmentBuffer.fnptr!;
        int segments = *(int*)(rd + 0x90);
        nint table = *(nint*)(rd + 0x94);
        if (segments <= 0 || segments > 4096 || table == 0) return;
        int prev = DynGeoSegment.Previous(cur, count);
        int firstArray = DynGeoSegment.Arrays[0].Offset;
        for (int i = 0; i < segments; i++) {
            nint seg = table + i * DynGeoSegment.Size;
            if (*(nint*)(seg + DynGeoSegment.At(firstArray, cur)) != 0 || *(nint*)(seg + DynGeoSegment.At(firstArray, prev)) == 0) continue;
            int vertices = *(int*)(seg + DynGeoSegment.At(DynGeoSegment.VertexCount, prev));
            int indices = *(int*)(seg + DynGeoSegment.At(DynGeoSegment.IndexCount, prev));
            if (vertices is <= 0 or > DynGeoSegment.MaxCount || indices is < 0 or > DynGeoSegment.MaxCount) continue;
            // Out of buffer memory: the game prints it and draws nothing more this frame; so does the replay.
            if (_segmentBuffer(rd, i, vertices, indices) == 0) return;
            foreach ((int Offset, int Bytes, bool PerIndex) array in DynGeoSegment.Arrays) {
                nint from = *(nint*)(seg + DynGeoSegment.At(array.Offset, prev)), to = *(nint*)(seg + DynGeoSegment.At(array.Offset, cur));
                long bytes = DynGeoSegment.Bytes(array, vertices, indices);
                if (from != 0 && to != 0 && bytes > 0) Buffer.MemoryCopy((void*)from, (void*)to, bytes, bytes);
            }
        }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_camera_process() {
        VoidFn? next = _nextCameraProcess ??= Chain(CameraProcess, h_camera_process, 0x007be110);
        if (next is null) return 0;
        return Redraw && !Failed ? 0 : next();
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_camera_set_player() {
        VoidFn? next = _nextCameraSetPlayer ??= Chain(CameraSetPlayer, h_camera_set_player, 0x0083f2e0);
        if (next is null) return 0;
        return Redraw && !Failed ? 0 : next();
    }

    /// <summary>
    ///     <c>Ch_CalcMiddle</c>: the characters' bones (<c>Ch_CalcMiddle_Batch</c> on the job workers, which hand them to
    ///     the renderer instances through <c>graphicCharacterInstanceSetSkeletonMatrices</c>) and their motion events.
    ///     Held on a Redraw frame. After a tick it is where each character's matrix and bones are final: they go into the
    ///     history (<see cref="CharacterHistory"/>).
    /// </summary>
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_calc_middle() {
        VoidFn? next = _nextCalcMiddle ??= Chain(CalcMiddle, h_calc_middle, 0x00833390);
        if (next is null) return 0;
        FrameSettings s = _settings();
        if (Redraw && !Failed && s.HoldCharacters) return 0;
        // Nothing is written into the bone arrays here: Ch_CalcMain queued the ordinary characters' bone jobs
        // (Ch_CalcMiddle_Batch pass 0) without waiting, and they may be writing those arrays now; Ch_CalcMiddle
        // waits for them first thing. After it returns every job is done (RecordCharacters).
        bool record = !Failed && _inFrame && _frame.Kind == FrameKind.Tick && _loopTicked;
        int r = next();
        if (record && !Failed) {
            try {
                _clothRecord = RecordCharacters(s) && s.InterpolateCloth && s.HoldCloth;
            }
            catch (Exception ex) {
                Fail("the characters' history", ex);
            }
        }
        return r;
    }

    // ------------------------------------------------------------------ the characters (between ticks)

    /// <summary>
    ///     Each character's matrix and its instances' bones after a tick, into the history; a new map starts it again.
    ///     True when a tick update was begun (the cloth is then recorded at the update's end).
    /// </summary>
    private bool RecordCharacters(FrameSettings s) {
        int map = _game.Map;
        if (map != _charMap || !s.InterpolateCharacters) {
            _charMap = map;
            _chars.Clear();
            _bonesWritten.Clear();
            _cloth.Clear();
            _clothWritten.Clear();
            if (!s.InterpolateCharacters) return false;
        }
        _chars.BeginTick();
        int n = _game.ChrCount;
        nint chars = _game.ChrArray;
        if (chars == 0 || n is <= 0 or > 4096) return true;
        for (int i = 0; i < n; i++) {
            nint c = chars + i * Game.ChrSize;
            if (*(byte*)(c + Game.ChrActive) == 0) continue;
            nint list = *(nint*)(c + Game.ChrInstances);
            _chars.PushRoot(c, ChrIdentity(c, list), *(Matrix4x4*)(c + Game.ChrMatrix), s.CharacterCutDistance, s.CharacterCutDegrees);
            if (!s.InterpolateBones) continue;
            PushBones(list);
            PushBones(*(nint*)(c + Game.ChrShadowInstances));
        }
        _chars.Prune();
        return true;
    }

    private static ulong ChrIdentity(nint c, nint list) => CharacterHistory.Identity(*(ushort*)(c + Game.ChrId), (uint)list);

    /// <summary>
    ///     The renderer instances of a character's instance list (<c>+0x830</c> or <c>+0x838</c>: count <c>[list] &amp;
    ///     0x7fffffff</c>, instances at <c>[list + 4]</c>) and each one's bone matrices: <c>[instance + 0xc]</c>, 64 bytes each,
    ///     <c>[[instance] + 0x18] &amp; 0x7fffffff</c> of them, as <c>graphicCharacterInstanceSetSkeletonMatrices</c> fills them.
    /// </summary>
    private static int Instances(nint list, Span<nint> into) {
        if (list == 0) return 0;
        uint n = *(uint*)list & 0x7fffffff;
        nint arr = *(nint*)(list + 4);
        if (arr == 0 || n == 0 || n > into.Length) return 0;
        for (int k = 0; k < n; k++) into[k] = *(nint*)(arr + 4 * k);
        return (int)n;
    }

    private static bool Bones(nint inst, out Matrix4x4* bones, out int count) {
        bones = null;
        count = 0;
        if (inst == 0) return false;
        nint mesh = *(nint*)inst;
        bones = *(Matrix4x4**)(inst + 0xc);
        if (mesh == 0 || bones == null) return false;
        count = *(int*)(mesh + 0x18) & 0x7fffffff;
        return count is > 0 and <= 1024;
    }

    /// <summary>
    ///     Records the instances' bones after a tick. An array that still holds exactly what the mod wrote between
    ///     ticks was not updated by the game this tick (the character is paused, hidden or still): its last true bones
    ///     are recorded again, not the interpolated ones.
    /// </summary>
    private void PushBones(nint list) {
        Span<nint> inst = stackalloc nint[64];
        int n = Instances(list, inst);
        for (int k = 0; k < n; k++) {
            if (!Bones(inst[k], out Matrix4x4* bones, out int count)) continue;
            ulong id = CharacterHistory.Identity((uint)(nint)bones, (uint)count);
            ReadOnlySpan<Matrix4x4> now = new(bones, count);
            if (_bonesWritten.Remove(inst[k], out Matrix4x4[]? written) && written.Length == count && now.SequenceEqual(written)) {
                if (_boneScratch.Length < count) _boneScratch = new Matrix4x4[count];
                Span<Matrix4x4> latest = _boneScratch.AsSpan(0, count);
                if (_chars.TryLatestBones(inst[k], id, latest)) {
                    latest.CopyTo(new Span<Matrix4x4>(bones, count));
                    _chars.PushBones(inst[k], id, latest);
                    continue;
                }
            }
            _chars.PushBones(inst[k], id, now);
        }
    }

    /// <summary>
    ///     <c>Ch_DrawMain</c>: draws every character, from its matrix (<c>+0x1d0</c>, through
    ///     <c>graphicCharacterInstanceSetWorldMatrix</c>) and its instances' bones. Each character the history can draw
    ///     between ticks gets the correction from its true matrix to the interpolated one (applied to the world matrices
    ///     the draw hands the renderer, <see cref="h_chr_set_world"/>), and its instances the interpolated bones. The
    ///     character's own matrix stays true: the cloth, the shadows, the cull and the game's logic read it.
    /// </summary>
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_draw_main() {
        VoidFn? next = _nextDrawMain ??= Chain(DrawMain, h_draw_main, 0x0082ed50);
        if (next is null) return 0;
        if (Failed) return next();
        FrameSettings s = _settings();
        // Between ticks only: a frame drawn at the tick itself (alpha 1, the game's own 30 fps) passes through.
        bool on = _inFrame && _frame.Kind is FrameKind.Tick or FrameKind.Redraw && _frame.Alpha < 1 && s.InterpolateCharacters;
        try {
            _rootDelta.Clear();
            _drawnRoot.Clear();
            if (on) Corrections(s, _frame.Alpha);
            WriteBones(s, _frame.Alpha, interpolate: on && s.InterpolateBones);
            // The cloth is drawn from FFXApplication_render's upload, after this update (h_cloth_upload).
            _clothBlend = on && s.InterpolateCloth && s.HoldCloth;
            _clothBones = s.InterpolateBones;
            _clothArmed = Environment.CurrentManagedThreadId == _gameThread && (_clothBlend || _clothWritten.Count > 0);
        }
        catch (Exception ex) {
            Fail("the characters between ticks", ex);
            _rootDelta.Clear();
            _drawnRoot.Clear();
            return next();
        }
        _inDrawMain = _rootDelta.Count > 0;
        try {
            return next();
        }
        finally {
            _inDrawMain = false;
        }
    }

    /// <summary>For each character drawn between ticks: true matrix⁻¹ × interpolated matrix, for both its instance lists.</summary>
    private void Corrections(FrameSettings s, double alpha) {
        int n = _game.ChrCount;
        nint chars = _game.ChrArray;
        if (chars == 0 || n is <= 0 or > 4096) return;
        for (int i = 0; i < n; i++) {
            nint c = chars + i * Game.ChrSize;
            if (*(byte*)(c + Game.ChrActive) == 0) continue;
            nint list = *(nint*)(c + Game.ChrInstances);
            ulong id = ChrIdentity(c, list);
            Matrix4x4 truth = *(Matrix4x4*)(c + Game.ChrMatrix);
            // Something moved it since the tick (a magic file's swap): draw it as it is.
            if (!_chars.TryLatestRoot(c, id, out Matrix4x4 latest) || CameraInterpolator.RelativeChange(latest, truth) > 1e-5) continue;
            if (!_chars.TryRoot(c, id, alpha, out Matrix4x4 drawn) || !Matrix4x4.Invert(truth, out Matrix4x4 inverse)) continue;
            Matrix4x4 delta = inverse * drawn;
            _drawnRoot[c] = drawn;
            if (list != 0) _rootDelta[list] = delta;
            nint shadow = *(nint*)(c + Game.ChrShadowInstances);
            if (shadow != 0) _rootDelta[shadow] = delta;
            Stats.Add(Counter.CharactersInterpolated);
        }
    }

    /// <summary>
    ///     Writes every character instance's bones: interpolated where the history allows (and the character is not a
    ///     warp), else the tick's own where the mod wrote them before. Only instances a character holds now are touched.
    /// </summary>
    private void WriteBones(FrameSettings s, double alpha, bool interpolate) {
        if (!interpolate && _bonesWritten.Count == 0) return;
        _bonesSeen.Clear();
        int n = _game.ChrCount;
        nint chars = _game.ChrArray;
        Span<nint> inst = stackalloc nint[64];
        if (chars != 0 && n is > 0 and <= 4096) {
            for (int i = 0; i < n; i++) {
                nint c = chars + i * Game.ChrSize;
                if (*(byte*)(c + Game.ChrActive) == 0) continue;
                nint list = *(nint*)(c + Game.ChrInstances);
                bool lerp = interpolate && !_chars.RootCut(c) && _chars.TryRoot(c, ChrIdentity(c, list), alpha, out _);
                for (int l = 0; l < 2; l++) {
                    int count = Instances(l == 0 ? list : *(nint*)(c + Game.ChrShadowInstances), inst);
                    for (int k = 0; k < count; k++) {
                        if (_bonesSeen.Add(inst[k])) WriteInstanceBones(inst[k], alpha, lerp);
                    }
                }
            }
        }
        // Instances no character holds any more: forget them (their memory may be gone).
        if (_bonesWritten.Count > _bonesSeen.Count) {
            foreach (nint k in new List<nint>(_bonesWritten.Keys)) {
                if (!_bonesSeen.Contains(k)) _bonesWritten.Remove(k);
            }
        }
    }

    private void WriteInstanceBones(nint inst, double alpha, bool lerp) {
        if (!Bones(inst, out Matrix4x4* bones, out int count)) return;
        ulong id = CharacterHistory.Identity((uint)(nint)bones, (uint)count);
        if (_boneScratch.Length < count) _boneScratch = new Matrix4x4[count];
        Span<Matrix4x4> scratch = _boneScratch.AsSpan(0, count);
        if (lerp && _chars.TryBones(inst, id, alpha, scratch)) {
            scratch.CopyTo(new Span<Matrix4x4>(bones, count));
            if (!_bonesWritten.TryGetValue(inst, out Matrix4x4[]? written) || written.Length != count) _bonesWritten[inst] = written = new Matrix4x4[count];
            scratch.CopyTo(written);
        }
        else if (_bonesWritten.Remove(inst) && _chars.TryLatestBones(inst, id, scratch)) {
            // Not interpolated now (a warp, the setting off): the tick's own bones again.
            scratch.CopyTo(new Span<Matrix4x4>(bones, count));
        }
    }

    /// <summary><c>graphicCharacterInstanceSetWorldMatrix(list, matrix)</c>: in <c>Ch_DrawMain</c>, a character drawn between ticks gets its correction.</summary>
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_chr_set_world(nint list, nint matrix) {
        PtrPtrFn? next = _nextChrSetWorld ??= Chain(ChrSetWorld, h_chr_set_world, 0x0063c3e0);
        if (next is null) return 0;
        nint m = Corrected(list, matrix);
        if (m != matrix) LogWorld(list, matrix, -1);
        return next(list, m);
    }

    /// <summary><c>graphicCharacterInstanceSetWorldMatrixByMdlID(list, matrix, model)</c>: the same for one model of the character.</summary>
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_chr_set_world_by_model(nint list, nint matrix, int model) {
        PtrPtrIntFn? next = _nextChrSetWorldByModel ??= Chain(ChrSetWorldByModel, h_chr_set_world_by_model, 0x0063c430);
        if (next is null) return 0;
        nint m = Corrected(list, matrix);
        if (m != matrix) LogWorld(list, matrix, model);
        return next(list, m, model);
    }

    /// <summary>
    ///     The world matrix to hand the renderer: <c>Ch_DrawMain</c>'s is (base element ×) scale × the character's matrix
    ///     (<c>FFXVu0MulMatrix(out, a, b)</c> is <c>b × a</c>), so the correction goes on the right. A copy: the draw passes
    ///     the same matrix on to the next call.
    /// </summary>
    private nint Corrected(nint list, nint matrix) {
        if (!_inDrawMain || matrix == 0 || Failed || Environment.CurrentManagedThreadId != _gameThread
            || !_rootDelta.TryGetValue(list, out Matrix4x4 delta)) return matrix;
        *_world = *(Matrix4x4*)matrix * delta;
        return (nint)_world;
    }

    // ------------------------------------------------------------------ the cloth (Redraw frames: held)

    /// <summary><c>JobSchedule_Ch_Cloth_PerCharacter</c>: <c>Ch_DrawMain</c> queues each cloth character's step (envelope, <c>Sg_ClothStep</c>, anchors) once per game update.</summary>
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_cloth_schedule(nint entry, int count) {
        PtrIntFn? next = _nextClothSchedule ??= Chain(ClothSchedule, h_cloth_schedule, 0x00a44b40);
        if (next is null) return 0;
        return Redraw && !Failed && _settings().HoldCloth ? 0 : next(entry, count);
    }

    /// <summary><c>Ch_DrawClothClear</c>: clears a cloth character's vertex records before its step refills them.</summary>
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_cloth_clear(nint chr) {
        PtrFn? next = _nextClothClear ??= Chain(ClothClear, h_cloth_clear, 0x00830b90);
        if (next is null) return 0;
        return Redraw && !Failed && _settings().HoldCloth ? 0 : next(chr);
    }

    // ------------------------------------------------------------------ camera cuts

    /// <summary>
    ///     <c>MsCameraResetMove(camera, flags)</c> (<c>0x007bed60</c>): stops a camera side's move and, unless
    ///     <c>flags</c> bit 3, puts it at its target now. The scripts' cut natives (<c>CamFocusCut</c> 0x6034,
    ///     <c>CamMoveFinish</c> 0x6016), <c>MsAtelResetMoveCameraID</c> and the magic files' <c>opu_camera</c> call it: the
    ///     game's own "this is a new shot". Noted for the update's camera sample (<see cref="CameraHistory.Push"/>).
    /// </summary>
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_camera_reset_move(int camera, int flags) {
        IntIntFn? next = _nextCameraResetMove ??= Chain(CameraResetMove, h_camera_reset_move, 0x007bed60);
        if (next is null) return 0;
        if (_inFrame && !Failed && (flags & 8) == 0) _cameraSnapped = true;
        return next(camera, flags);
    }

    /// <summary>
    ///     The camera cut at this tick (a new shot, by the thresholds or the game's instant set): every character and its
    ///     cloth is drawn as the tick left it, like a warp, until the next tick. <c>Ch_DrawMain</c> ran before the camera
    ///     (<c>MsScreenProcess</c>) in this update, so what it handed the renderer between ticks is put back: the true
    ///     world matrices sent again, the tick's bones written back. The cloth's upload comes after the update and finds
    ///     no character to blend.
    /// </summary>
    private void CutCharacters(FrameSettings s) {
        _chars.CutAll();
        _cloth.CutAll(_chars.Serial);
        _rootDelta.Clear();
        _drawnRoot.Clear();
        if (_worldCalls.Count > 0 && Environment.CurrentManagedThreadId == _gameThread
            && _worldCalls.Take(_updateSerial, ListIntact, _worldReplay) > 0) {
            PtrPtrFn? set = _nextChrSetWorld;
            PtrPtrIntFn? byModel = _nextChrSetWorldByModel;
            foreach (WorldMatrixLog.Entry e in _worldReplay) {
                *_world = e.Truth;
                if (e.Model < 0) set?.Invoke(e.List, (nint)_world);
                else byModel?.Invoke(e.List, (nint)_world, e.Model);
            }
            _worldReplay.Clear();
        }
        WriteBones(s, _frame.Alpha, interpolate: false);
    }

    /// <summary>Logs a world matrix the mod corrected in <c>Ch_DrawMain</c>, with the list as it is now.</summary>
    private void LogWorld(nint list, nint matrix, int model) {
        if (list == 0 || matrix == 0) return;
        _worldCalls.Add(_updateSerial, new WorldMatrixLog.Entry(list, *(Matrix4x4*)matrix, model, *(uint*)list & 0x7fffffff, *(nint*)(list + 4)));
    }

    /// <summary>
    ///     Whether a logged list may be written again: still the instance list (<c>+0x830</c> or <c>+0x838</c>) of an active
    ///     character, with the count and instance array it was logged with and no null instance. Anything else is left as
    ///     the game has it.
    /// </summary>
    private bool ListIntact(WorldMatrixLog.Entry e) {
        if (!IsCharacterList(e.List)) return false;
        nint arr = *(nint*)(e.List + 4);
        return WorldMatrixLog.Intact(e, *(uint*)e.List, arr, i => *(nint*)(arr + 4 * i));
    }

    private bool IsCharacterList(nint list) {
        int n = _game.ChrCount;
        nint chars = _game.ChrArray;
        if (list == 0 || chars == 0 || n is <= 0 or > 4096) return false;
        for (int i = 0; i < n; i++) {
            nint c = chars + i * Game.ChrSize;
            if (*(byte*)(c + Game.ChrActive) == 0) continue;
            if (*(nint*)(c + Game.ChrInstances) == list || *(nint*)(c + Game.ChrShadowInstances) == list) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ the cloth (between ticks)

    /// <summary>
    ///     A character's cloth meshes: its render cloth (<c>chr + 0x834</c>) in its set-up state (<c>+0xc</c> 2, as
    ///     <c>Ch_UpdateClothMatrix</c> leaves it), <c>+0x14</c> lists from <c>+0x30</c>, each a <c>u32</c> count and mesh
    ///     pointers.
    /// </summary>
    private static int ClothMeshes(nint chr, Span<nint> into) {
        // As Ch_UpdateClothMatrix and Sg_ClothPlaceAnchors: a render cloth and the cloth set up (flags bit 21).
        if ((*(uint*)(chr + Game.ChrFlags) & Game.ChrClothReady) == 0) return 0;
        nint rc = *(nint*)(chr + Game.ChrCloth);
        if (rc == 0 || (rc & 3) != 0 || *(int*)(rc + 0xc) != 2) return 0;
        uint lists = *(uint*)(rc + 0x14);
        if (lists > 128) return 0;
        int n = 0;
        for (int i = 0; i < lists; i++) {
            nint list = *(nint*)(rc + 0x30 + 4 * i);
            if (list == 0) continue;
            if ((list & 3) != 0) return 0;
            uint count = *(uint*)list;
            if (count > 256) return 0;
            for (int k = 0; k < count && n < into.Length; k++) {
                nint mesh = *(nint*)(list + 4 + 4 * k);
                if ((mesh & 3) != 0) return 0;
                if (mesh != 0) into[n++] = mesh;
            }
        }
        return n;
    }

    /// <summary>A cloth mesh's drawn vertices: <c>+0x44</c> of them at <c>[+0x4c]</c>, 16 bytes each (<c>Sg_ClothPlaceAnchors</c>' output).</summary>
    private static bool ClothVertices(nint mesh, out Vector4* vertices, out int count) {
        count = *(int*)(mesh + 0x44);
        vertices = *(Vector4**)(mesh + 0x4c);
        // The vertex buffers come from the cloth heap, 16-aligned (graphicCharacterInstanceGetClothVertexBuffers).
        return vertices != null && ((nint)vertices & 0xf) == 0 && count is > 0 and <= 65536;
    }

    private Span<Vector4> ClothScratch(int count) {
        if (_clothScratch.Length < count) _clothScratch = new Vector4[count];
        return _clothScratch.AsSpan(0, count);
    }

    /// <summary>
    ///     The end of a game update with a tick: each cloth mesh's vertices and local matrix into the history. A mesh the
    ///     game did not write this update (it still holds what the mod wrote: the character hidden or its cloth not stepped)
    ///     gets its last true sample back and records that again, not the blend.
    /// </summary>
    private void RecordCloth() {
        int serial = _chars.Serial;
        int n = _game.ChrCount;
        nint chars = _game.ChrArray;
        if (chars == 0 || n is <= 0 or > 4096) return;
        Span<nint> meshes = stackalloc nint[256];
        for (int i = 0; i < n; i++) {
            nint c = chars + i * Game.ChrSize;
            if (*(byte*)(c + Game.ChrActive) == 0) continue;
            int m = ClothMeshes(c, meshes);
            if (m == 0) continue;
            Matrix4x4 root = *(Matrix4x4*)(c + Game.ChrMatrix);
            for (int k = 0; k < m; k++) {
                nint mesh = meshes[k];
                if (!ClothVertices(mesh, out Vector4* v, out int count)) continue;
                ulong id = CharacterHistory.Identity((uint)(nint)v, (uint)count);
                Span<Vector4> live = new(v, count);
                Matrix4x4* world = (Matrix4x4*)mesh;
                Span<Vector4> latest = ClothScratch(count);
                Matrix4x4 local;
                if (_clothWritten.Remove(mesh, out (Matrix4x4 World, Vector4[] Vertices) written)
                    && _cloth.TryLatest(mesh, id, out Matrix4x4 latestLocal, latest)) {
                    if (written.Vertices.Length == count && live.SequenceEqual(written.Vertices)) latest.CopyTo(live);
                    if (*world == written.World) {
                        local = latestLocal;
                        *world = ClothHistory.World(local, root);
                    }
                    else if (!ClothHistory.Local(*world, root, out local)) continue;
                }
                else if (!ClothHistory.Local(*world, root, out local)) continue;
                _cloth.Push(mesh, id, serial, local, live);
            }
        }
        _cloth.Prune(serial);
    }

    /// <summary>
    ///     <c>graphicCharacterInstanceSetClothVertexBuffers</c> (<c>0x0063b740</c>, <c>FFXApplication_render</c>, main
    ///     thread, after the update): hands each cloth mesh's matrix and vertices to the renderer (<c>batchSetClothVertexBuffers</c>,
    ///     whose submit copies the vertices into the draw's stream). Before it runs, each cloth mesh of a character drawn
    ///     between ticks gets the blend of its last two ticks, placed with the character as drawn.
    /// </summary>
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_cloth_upload() {
        VoidFn? next = _nextClothUpload ??= Chain(ClothUpload, h_cloth_upload, 0x0063b740);
        if (next is null) return 0;
        if (_clothArmed && !Failed && Environment.CurrentManagedThreadId == _gameThread) {
            _clothArmed = false;
            try {
                WriteCloth(_frame.Alpha, _clothBlend, _clothBones);
            }
            catch (Exception ex) {
                Fail("the cloth between ticks", ex);
            }
        }
        return next();
    }

    /// <summary>
    ///     Writes every cloth mesh of a character drawn between ticks: blended where the history has its last two ticks
    ///     and the bones are interpolated too, else the tick's cloth carried along with the character; a mesh the mod wrote
    ///     before and does not blend now gets its tick's own vertices back.
    /// </summary>
    private void WriteCloth(double alpha, bool blend, bool bones) {
        if (!blend && _clothWritten.Count == 0) return;
        _clothSeen.Clear();
        int serial = _chars.Serial;
        int n = _game.ChrCount;
        nint chars = _game.ChrArray;
        Span<nint> meshes = stackalloc nint[256];
        if (chars != 0 && n is > 0 and <= 4096) {
            for (int i = 0; i < n; i++) {
                nint c = chars + i * Game.ChrSize;
                if (*(byte*)(c + Game.ChrActive) == 0) continue;
                int m = ClothMeshes(c, meshes);
                if (m == 0) continue;
                Matrix4x4 drawn = default;
                bool lerp = blend && _drawnRoot.TryGetValue(c, out drawn);
                Matrix4x4 root = *(Matrix4x4*)(c + Game.ChrMatrix);
                for (int k = 0; k < m; k++) {
                    nint mesh = meshes[k];
                    if (!_clothSeen.Add(mesh) || !ClothVertices(mesh, out Vector4* v, out int count)) continue;
                    ulong id = CharacterHistory.Identity((uint)(nint)v, (uint)count);
                    WriteMesh(mesh, id, new Span<Vector4>(v, count), serial, alpha, lerp, bones, root, drawn);
                }
            }
        }
        // Meshes no character holds any more: forget them (their memory may be gone).
        if (_clothWritten.Count > _clothSeen.Count) {
            foreach (nint k in new List<nint>(_clothWritten.Keys)) {
                if (!_clothSeen.Contains(k)) _clothWritten.Remove(k);
            }
        }
    }

    private void WriteMesh(nint mesh, ulong id, Span<Vector4> live, int serial, double alpha, bool lerp, bool bones, in Matrix4x4 root, in Matrix4x4 drawn) {
        int count = live.Length;
        Matrix4x4* world = (Matrix4x4*)mesh;
        Span<Vector4> scratch = ClothScratch(count);
        if (lerp) {
            Matrix4x4 local;
            if (bones && _cloth.TryBlend(mesh, id, serial, alpha, out local, scratch)) {
                scratch.CopyTo(live);
                Stats.Add(Counter.ClothInterpolated);
            }
            // One sample only (new, or its vertex buffer changed), or the bones drawn as they are: the tick's cloth goes
            // along with the character.
            else if (!ClothHistory.Local(*world, root, out local)) return;
            *world = ClothHistory.World(local, drawn);
            if (!_clothWritten.TryGetValue(mesh, out (Matrix4x4 World, Vector4[] Vertices) w) || w.Vertices.Length != count) w.Vertices = new Vector4[count];
            live.CopyTo(w.Vertices);
            w.World = *world;
            _clothWritten[mesh] = w;
        }
        else if (_clothWritten.Remove(mesh) && _cloth.TryLatest(mesh, id, out Matrix4x4 latest, scratch)) {
            // Not between ticks now (a warp, the setting off): the tick's own cloth again.
            scratch.CopyTo(live);
            *world = ClothHistory.World(latest, root);
        }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_yi_anim() {
        VoidFn? next = _nextYi ??= Chain(YiAnimMain, h_yi_anim, 0x00908320);
        if (next is null) return 0;
        return Redraw && !Failed && _settings().HoldFieldAnimation ? 0 : next();
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_video_update(double dt) {
        DoubleFn? next = _nextVideo ??= Chain(VideoUpdate, h_video_update, 0x00645df0);
        if (next is null) return 0;
        return Redraw && !Failed ? 0 : next(dt);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_draw_filter() {
        VoidFn? next = _nextDrawFilter ??= Chain(DrawFilter, h_draw_filter, 0x0082c0c0);
        if (next is null) return 0;
        if (!Redraw || Failed || !_settings().HoldFades) return next();
        // Draw the fades as they are, then put their counters back: they step once per tick, not per drawn frame.
        _game.SaveFades(_fades);
        int r = next();
        _game.RestoreFades(_fades);
        return r;
    }

    // ------------------------------------------------------------------ counters

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_calc_rate() {
        VoidFn? next = _nextCalcRate ??= Chain(MainCalcRate, h_calc_rate, 0x008206c0);
        if (next is null) return 0;
        // Sg_MainLoop calls it only on the way to the world update (not in the menu, a pause or a load).
        _worldSeen = true;
        return next();
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_event_jump() {
        VoidFn? next = _nextEventJump ??= Chain(EventJump, h_event_jump, 0x0088d3d0);
        if (next is null) return 0;
        Stats.Add(Counter.WorldTicks);
        return next();
    }

    // ------------------------------------------------------------------ the camera

    /// <summary>
    ///     <c>Cam_Apply</c> hands the renderer the camera that <c>MsScreenProcess</c> just computed into
    ///     <c>g_CameraMatrix</c>, during the draw of every game update. After a tick that is the tick's camera: it goes
    ///     into the history. Then the matrices are swapped for the interpolated ones, for the renderer and for the
    ///     rest of the draw (effects, the field's view), until the next update starts (<see cref="h_main_loop"/>).
    /// </summary>
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_set_world_matrix(nint matrix) {
        PtrFn? next = _nextSetWorld ??= Chain(SetWorldMatrix, h_set_world_matrix, 0x00639860);
        if (next is null) return 0;
        if (Failed || _settingCamera || !_inFrame || _frame.Kind == FrameKind.Vanilla || matrix != _game.CameraMatrixAddress) return next(matrix);
        try {
            FrameSettings s = _settings();
            if (_loopTicked && !_pushedThisLoop) {
                _camera.Push(_game.CameraMatrix, _game.ScreenMatrix, s.CutDistance, s.CutDegrees, _cameraSnapped && s.ScriptCameraCuts);
                _pushedThisLoop = true;
                if (_camera.Cut) CutCharacters(s);
            }
            if (s.Interpolate && _frame.Alpha < 1 && !_game.MovieOn && !_game.WaitFramesByTime && !_game.PartMatrix
                && _camera.TryAt(_frame.Alpha, out Matrix4x4 cam, out Matrix4x4 scr)
                && Same(_camera, _game.CameraMatrix)) {
                if (!_applied) {
                    _trueCam = _game.CameraMatrix;
                    _trueScr = _game.ScreenMatrix;
                    _applied = true;
                }
                _game.CameraMatrix = cam;
                _game.ScreenMatrix = scr;
            }
        }
        catch (Exception ex) {
            Fail("the camera", ex);
        }
        return next(matrix);
    }

    /// <summary>Whether <paramref name="m"/> is the history's latest camera (and not a second screen's).</summary>
    private static bool Same(CameraHistory h, in Matrix4x4 m) => h.TryLatest(out Matrix4x4 latest) && CameraInterpolator.RelativeChange(latest, m) < 1e-4;

    /// <summary>A Repeat frame: no game update, so the renderer's camera is set here, from the history.</summary>
    private void MoveCameraWithoutUpdate(double alpha) {
        if (!_camera.TryAt(alpha, out Matrix4x4 cam, out Matrix4x4 scr)) return;
        if (!_applied) {
            _trueCam = _game.CameraMatrix;
            _trueScr = _game.ScreenMatrix;
            if (!Same(_camera, _trueCam)) return;
            _applied = true;
        }
        _game.CameraMatrix = cam;
        _game.ScreenMatrix = scr;
        PushToRenderer();
    }

    private void RestoreCamera() {
        _game.CameraMatrix = _trueCam;
        _game.ScreenMatrix = _trueScr;
        _applied = false;
        // The renderer gets the true camera again from the next draw's Cam_Apply; a Repeat frame set it directly.
        if (!_inFrame) PushToRenderer();
    }

    private void PushToRenderer() {
        PtrFn? next = _nextSetWorld ??= Chain(SetWorldMatrix, h_set_world_matrix, 0x00639860);
        if (next is null) return;
        _settingCamera = true;
        try {
            next(_game.CameraMatrixAddress);
        }
        finally {
            _settingCamera = false;
        }
    }

    // ------------------------------------------------------------------ vsync (render thread)

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private byte h_present_flip(nint renderer) {
        ThisFn? next = _nextPresent ??= Chain(PresentFlip, h_present_flip, 0x00593760);
        if (next is null) return 0;
        Stats.Add(Counter.Presents);
        int want = _vsyncWanted;
        if (want < 0 || renderer == 0 || Failed) return next(renderer);
        byte* flag = (byte*)renderer + Game.RendererVsync;
        byte saved = *flag;
        *flag = (byte)want;
        try {
            return next(renderer);
        }
        finally {
            *flag = saved;
        }
    }
}
