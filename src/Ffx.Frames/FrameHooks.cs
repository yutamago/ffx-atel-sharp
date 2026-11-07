// SPDX-License-Identifier: MIT

using System;
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
///         <item><c>updateFFX</c> (<c>0x00822840</c>, once per drawn frame): decides the frame; a Repeat frame skips it.</item>
///         <item><c>Sg_MainLoop</c> (<c>0x00820ae0</c>, once per game update): puts the true camera back.</item>
///         <item><c>Sg_FrameSync</c> (<c>0x00821e80</c>): sets the updates (<c>g_VoiceHold</c>) and each update's ticks (exec frames); not run at all on a Redraw frame.</item>
///         <item>The holds (Redraw frames): <c>TkUpdateScanControler</c>, <c>Sg_ControlMain</c>, <c>MsEffectProcess</c>, <c>MsCameraProcess</c>, <c>Sg_CameraSetPlayer</c>, <c>Ch_CalcMiddle</c>, <c>yiAnimMain</c>, <c>graphicVideoUpdate</c>, <c>op_loop_ffx_after</c>, <c>Sg_DrawFilter</c>.</item>
///         <item><c>graphicCameraSetWorldMatrix</c> (<c>0x00639860</c>, from <c>Cam_Apply</c> in the draw's <c>MsScreenProcess</c>): records the tick's camera and swaps in the interpolated one.</item>
///         <item><c>Renderer_PresentFlip</c> (<c>0x00593760</c>, render thread): vsync on or off.</item>
///     </list>
///     <para>Any exception in a hook switches the mod to pass-through for the rest of the session, logged once.</para>
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

    private static FhCallHandle<T> At<T>(uint va) where T : Delegate => new(new FhCallLocation("FFX.exe", Game.Offset(va)));

    // Hooked.
    private static FhCallHandle<FloatFn> UpdateFfx => At<FloatFn>(0x00822840);
    private static FhCallHandle<MainLoopFn> MainLoop => At<MainLoopFn>(0x00820ae0);
    private static FhCallHandle<FloatFn> FrameSync => At<FloatFn>(0x00821e80);
    private static FhCallHandle<VoidFn> UpdateScan => At<VoidFn>(0x00889a80);
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
    // Called.
    private static FhCallHandle<VoidFn> CanSpeedUp => At<VoidFn>(0x0081fd30);
    private static FhCallHandle<IntFn> SetMuteSound => At<IntFn>(0x00821c60);
    private static FhCallHandle<ByteIntFn> IsMapExcluded => At<ByteIntFn>(0x006f6fa0);
    private static FhCallHandle<ByteFn> IsMagicExcluded => At<ByteFn>(0x006f6ff0);
    private static FhCallHandle<VoidFn> BattleStatus => At<VoidFn>(0x007816f0);
    private static FhCallHandle<VoidFn> SphereGrid => At<VoidFn>(0x00641060);

    private readonly Game _game;
    private readonly FhLogger _log;
    private readonly Func<FrameSettings> _settings;
    private readonly FrameDirector _director = new();
    private readonly CameraHistory _camera = new();
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
    public volatile string Status = "starting";
    public bool Failed => Volatile.Read(ref _failed) != 0;
    public CameraHistory Camera => _camera;
    public bool LimiterPatched => _limiterPatched;

    private FloatFn? _nextUpdate, _nextFrameSync;
    private MainLoopFn? _nextMainLoop;
    private VoidFn? _nextUpdateScan, _nextControlMain, _nextCameraProcess, _nextCameraSetPlayer, _nextCalcMiddle, _nextYi, _nextOpLoopAfter, _nextDrawFilter, _nextCalcRate, _nextEventJump;
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
        _limiterVsyncs = (int*)NativeMemory.AllocZeroed(16);
        _limiterPerVsync = (double*)NativeMemory.AllocZeroed(16);
        *_limiterVsyncs = 2;
        *_limiterPerVsync = FramePacing.GameVsync;
        _fades = (byte*)NativeMemory.AllocZeroed(Game.FadeStateSize);
    }

    /// <summary>
    ///     The first bytes of every function the mod hooks, as in the Steam release this was written against
    ///     (<c>??</c>: an absolute address, which a rebased image changes). A different game version fails the check
    ///     and the mod installs nothing.
    /// </summary>
    private static readonly (uint Va, string Name, string Bytes)[] Prologues = [
        (0x00822840, "updateFFX", "55 8b ec 83 3d ?? ?? ?? ?? 00 74 05"),
        (0x00820ae0, "Sg_MainLoop", "55 8b ec 81 ec a4 00 00 00"),
        (0x00821e80, "Sg_FrameSync", "55 8b ec 83 ec 18 d9 05 ?? ?? ?? ?? d8 45 08"),
        (0x00889a80, "TkUpdateScanControler", "56 33 f6 6a 00 56 e8"),
        (0x0082d070, "Sg_ControlMain", "6a 00 6a 00 6a 00 e8"),
        (0x00787e00, "MsEffectProcess", "55 8b ec 53 8b 5d 08 8b c3 56"),
        (0x007be110, "MsCameraProcess", "55 8b ec 83 ec 44 a1"),
        (0x0083f2e0, "Sg_CameraSetPlayer", "53 8b dc 83 ec 08 83 e4 f0"),
        (0x00833390, "Ch_CalcMiddle", "55 8b ec 83 ec 08 a1"),
        (0x00908320, "yiAnimMain", "68 ?? ?? ?? ?? e8 ?? ?? ?? ?? 59 c3"),
        (0x00645df0, "graphicVideoUpdate", "e8 ?? ?? ?? ?? 80 b8 d0 06 00 00 00"),
        (0x007ff680, "op_loop_ffx_after", "53 8b dc 83 ec 08 83 e4 f0"),
        (0x0082c0c0, "Sg_DrawFilter", "55 8b ec 83 ec 0c e8"),
        (0x008206c0, "Sg_MainCalcRate", "55 8b ec 51 a1"),
        (0x0088d3d0, "TkUpdateEventJump", "83 3d ?? ?? ?? ?? 00 74 6d 56 e8"),
        (0x00639860, "graphicCameraSetWorldMatrix", "55 8b ec 81 ec c0 00 00 00"),
        (0x00593760, "Renderer_PresentFlip", "56 8b f1 8b 56 74 85 d2"),
    ];

    /// <summary>Whether every hooked function starts as expected; logs each that does not.</summary>
    public bool CheckGame() {
        bool ok = true;
        foreach ((uint va, string name, string bytes) in Prologues) {
            string[] want = bytes.Split(' ');
            for (int i = 0; i < want.Length; i++) {
                if (want[i] == "??" || _game.CodeByte(va + (uint)i) == Convert.ToByte(want[i], 16)) continue;
                _log.Error($"Frames: {name} (0x{va:x8}) is not the code this mod knows (byte {i}); another game version?");
                ok = false;
                break;
            }
        }
        return ok;
    }

    /// <summary>Installs every hook (in <c>init</c>: Fahrenheit applies them after all modules are initialised). False and a log line for each that failed.</summary>
    public bool Install(FhModule owner) {
        bool ok = true;
        void Hook(bool done, string name) {
            if (done) return;
            ok = false;
            _log.Error($"Frames: cannot hook {name}");
        }
        Hook(UpdateFfx.hook(owner, h_update_ffx), "updateFFX");
        Hook(MainLoop.hook(owner, h_main_loop), "Sg_MainLoop");
        Hook(FrameSync.hook(owner, h_frame_sync), "Sg_FrameSync");
        Hook(UpdateScan.hook(owner, h_update_scan), "TkUpdateScanControler");
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
        return ok;
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

    private void Fail(string where, Exception ex) {
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        _log.Error($"Frames: {where} failed; the mod passes everything through to the game from now on: {ex}");
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
        _nextUpdate ??= UpdateFfx.chain_from(h_update_ffx).fnptr!;
        if (Failed) return _nextUpdate(dt);
        FrameSettings s = _settings();
        try {
            BeginFrame(dt, s);
        }
        catch (Exception ex) {
            Fail("the frame decision", ex);
            _inFrame = false;
            return _nextUpdate(dt);
        }
        Stats.Add(Counter.Frames);
        if (_frame.Kind == FrameKind.Repeat) {
            Stats.Add(Counter.RepeatFrames);
            try {
                if (s.Interpolate && s.InterpolateRepeats && !_game.MovieOn && !_game.WaitFramesByTime) MoveCameraWithoutUpdate(_frame.Alpha);
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
        try {
            return _nextUpdate(dt);
        }
        finally {
            _inFrame = false;
            if (_frame.Kind != FrameKind.Vanilla) {
                _game.ExecFrames = _execSaved;
                _worldActive = _worldSeen;
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
            _game.ClearCarries();
            _log.Info($"Frames: {(s.Enabled ? "on" : "off")}");
        }
        ApplyLimiter(s);
        _vsyncWanted = !s.Enabled ? -1 : s.Vsync switch { VsyncMode.On => 1, VsyncMode.Off => 0, _ => -1 };

        int vsyncs = _game.FrameVsyncs;
        bool movie = _game.MovieOn || _game.WaitFramesByTime;
        string? excluded = null;
        if (s.Enabled && Math.Abs(s.TimeScale - 1) > 1e-9) excluded = Excluded(s);
        ExcludedReason = excluded;
        FrameInput input = new(dt, vsyncs, _worldActive, movie, excluded is not null, s.Enabled ? _game.Booster : 1);
        _frame = _director.Next(input, s);
        _execSaved = _game.ExecFrames;
        if (_execSaved < 1) _execSaved = 1;
        LastAlpha = (float)_frame.Alpha;
        LastScale = (float)_frame.Scale;
        LastTicks = _frame.Plan.Ticks;
        Stats.Add(Counter.PlannedTicks, _frame.Plan.Ticks);
        if (_frame.Kind == FrameKind.Vanilla && _applied) RestoreCamera();
        if (_frame.Kind == FrameKind.Vanilla) _camera.Clear();
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
        _nextMainLoop ??= MainLoop.chain_from(h_main_loop).fnptr!;
        if (Failed) {
            _nextMainLoop(dt);
            return;
        }
        bool redraw = Redraw;
        int calm = 0;
        try {
            if (_applied) RestoreCamera();
            _pushedThisLoop = false;
            _loopTicked = false;
            if (redraw) calm = _game.CalmFrames;
        }
        catch (Exception ex) {
            Fail("Sg_MainLoop", ex);
        }
        Stats.Add(Counter.Updates);
        _nextMainLoop(dt);
        if (redraw && !Failed) _game.CalmFrames = calm;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_frame_sync(float dt) {
        _nextFrameSync ??= FrameSync.chain_from(h_frame_sync).fnptr!;
        if (Failed || !_inFrame || _frame.Kind is FrameKind.Vanilla or FrameKind.Repeat) return _nextFrameSync(dt);
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
                int r = _nextFrameSync(dt);
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
            int rest = _nextFrameSync(dt);
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
        _nextUpdateScan ??= UpdateScan.chain_from(h_update_scan).fnptr!;
        return Redraw && !Failed ? 0 : _nextUpdateScan();
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_control_main() {
        _nextControlMain ??= ControlMain.chain_from(h_control_main).fnptr!;
        return Redraw && !Failed ? 0 : _nextControlMain();
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_effect_process(int half) {
        _nextEffect ??= EffectProcess.chain_from(h_effect_process).fnptr!;
        if (Redraw && !Failed && (half == 0 || _settings().HoldEffectsDraw)) return 0;
        return _nextEffect(half);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_op_loop_after() {
        _nextOpLoopAfter ??= OpLoopAfter.chain_from(h_op_loop_after).fnptr!;
        return Redraw && !Failed && _settings().HoldEffectsDraw ? 0 : _nextOpLoopAfter();
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_camera_process() {
        _nextCameraProcess ??= CameraProcess.chain_from(h_camera_process).fnptr!;
        return Redraw && !Failed ? 0 : _nextCameraProcess();
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_camera_set_player() {
        _nextCameraSetPlayer ??= CameraSetPlayer.chain_from(h_camera_set_player).fnptr!;
        return Redraw && !Failed ? 0 : _nextCameraSetPlayer();
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_calc_middle() {
        _nextCalcMiddle ??= CalcMiddle.chain_from(h_calc_middle).fnptr!;
        return Redraw && !Failed && _settings().HoldCharacters ? 0 : _nextCalcMiddle();
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_yi_anim() {
        _nextYi ??= YiAnimMain.chain_from(h_yi_anim).fnptr!;
        return Redraw && !Failed && _settings().HoldFieldAnimation ? 0 : _nextYi();
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_video_update(double dt) {
        _nextVideo ??= VideoUpdate.chain_from(h_video_update).fnptr!;
        return Redraw && !Failed ? 0 : _nextVideo(dt);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_draw_filter() {
        _nextDrawFilter ??= DrawFilter.chain_from(h_draw_filter).fnptr!;
        if (!Redraw || Failed || !_settings().HoldFades) return _nextDrawFilter();
        // Draw the fades as they are, then put their counters back: they step once per tick, not per drawn frame.
        _game.SaveFades(_fades);
        int r = _nextDrawFilter();
        _game.RestoreFades(_fades);
        return r;
    }

    // ------------------------------------------------------------------ counters

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_calc_rate() {
        _nextCalcRate ??= MainCalcRate.chain_from(h_calc_rate).fnptr!;
        // Sg_MainLoop calls it only on the way to the world update (not in the menu, a pause or a load).
        _worldSeen = true;
        return _nextCalcRate();
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_event_jump() {
        _nextEventJump ??= EventJump.chain_from(h_event_jump).fnptr!;
        Stats.Add(Counter.WorldTicks);
        return _nextEventJump();
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
        _nextSetWorld ??= SetWorldMatrix.chain_from(h_set_world_matrix).fnptr!;
        if (Failed || _settingCamera || !_inFrame || _frame.Kind == FrameKind.Vanilla || matrix != _game.CameraMatrixAddress) return _nextSetWorld(matrix);
        try {
            FrameSettings s = _settings();
            if (_loopTicked && !_pushedThisLoop) {
                _camera.Push(_game.CameraMatrix, _game.ScreenMatrix, s.CutDistance, s.CutDegrees);
                _pushedThisLoop = true;
            }
            if (s.Interpolate && !_game.MovieOn && !_game.WaitFramesByTime && !_game.PartMatrix
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
        return _nextSetWorld(matrix);
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
        _nextSetWorld ??= SetWorldMatrix.chain_from(h_set_world_matrix).fnptr!;
        _settingCamera = true;
        try {
            _nextSetWorld(_game.CameraMatrixAddress);
        }
        finally {
            _settingCamera = false;
        }
    }

    // ------------------------------------------------------------------ vsync (render thread)

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private byte h_present_flip(nint renderer) {
        _nextPresent ??= PresentFlip.chain_from(h_present_flip).fnptr!;
        Stats.Add(Counter.Presents);
        int want = _vsyncWanted;
        if (want < 0 || renderer == 0 || Failed) return _nextPresent(renderer);
        byte* flag = (byte*)renderer + Game.RendererVsync;
        byte saved = *flag;
        *flag = (byte)want;
        try {
            return _nextPresent(renderer);
        }
        finally {
            *flag = saved;
        }
    }
}
