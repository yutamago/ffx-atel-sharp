// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Fahrenheit;
using FhCall = Fahrenheit.FFX.FhCall;

using Ffx.Atel.Linker;

namespace Ffx.Atel.Loader;

/// <summary>
///     The mods' saved fields in the game (docs/research/mod-saves.md, "The loader at run time"): the save's mod area is
///     their home while the game runs; this fills it on a new game and a load, keeps it by key in the loader's Fahrenheit
///     local state on every save, and zeroes it in the file the game writes, so save files stay vanilla.
/// </summary>
/// <remarks>
///     <list type="table">
///         <item><term>New game</term><description>after <c>MsSetSaveStartGame</c> (<c>0x00786b00</c>), which clears the save
///         block: every field's initial value (Fahrenheit does not raise <c>PostNewGame</c> yet).</description></item>
///         <item><term>Load</term><description><c>load_local_state</c> (only when the slot has a state file), then
///         <c>PostLoadGame</c>: the area is rebuilt from the state by key; without a state, from the initial values.</description></item>
///         <item><term>Write</term><description>before <c>SaveDataWriteCrc</c> (<c>0x00648f20</c>), which every save path
///         (the game's and Fahrenheit's, the autosave too) calls with the file buffer: file bytes <c>0x64f8</c>..<c>0x6900</c>
///         are zeroed there; <c>g_SaveData</c> keeps them. The file's fingerprint is taken after the CRC is written.</description></item>
///         <item><term>Save</term><description><c>save_local_state</c>, after the file is written: the state, with that
///         fingerprint.</description></item>
///     </list>
/// </remarks>
internal sealed unsafe class SaveHooks(SavedState state, FhLogger log) {
    // Offsets from the image base (FFX.exe at 0x00400000).
    /// <summary><c>g_SaveData</c> (<c>0x0112ca90</c>): the save block the game runs on.</summary>
    private const nint SaveData = 0x00d2ca90;

    /// <summary>The save manager's file buffer (<c>0x01597f70</c>, Fahrenheit's <c>pal_addr_buf_save</c>): the file a load read, header included.</summary>
    private const nint SaveFileBuffer = 0x01197f70;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint WriteCrcFn(byte* file);

    /// <summary><c>SaveDataWriteCrc</c> (<c>0x00648f20</c>): the CRC over the file buffer's block, stored at <c>+0x1a</c> and <c>+0x64f4</c>.</summary>
    private static FhCallHandle<WriteCrcFn> SaveDataWriteCrc => new(new FhCallLocation("FFX.exe", 0x00248f20));

    private FhCall.d_MsSetSaveStartGame? _new_game_next;
    private WriteCrcFn? _write_crc_next;
    private SaveFingerprint? _written;
    private bool _state_loaded;

    public SavedState State => state;

    /// <summary>The mod area in <c>g_SaveData</c>.</summary>
    private static Span<byte> Area => new(FhUtil.ptr_at<byte>(SaveData + SaveArea.BlockOffset), SaveArea.Length);

    /// <summary>Puts the hooks on; false (with an error in the log) for each that Fahrenheit could not.</summary>
    public bool Install(FhModule owner) {
        bool newGame = FhCall.MsSetSaveStartGame.hook(owner, h_new_game);
        bool writeCrc = SaveDataWriteCrc.hook(owner, h_write_crc);
        if (!newGame) log.Error("Atel loader: cannot hook MsSetSaveStartGame; a new game starts the saved fields at 0, not their initial values.");
        if (!writeCrc) log.Error("Atel loader: cannot hook SaveDataWriteCrc; save files keep the saved fields' bytes in their tail.");
        FhApi.Events.Common.GameLoop.PostLoadGame.subscribe(_ => PostLoad());
        return newGame && writeCrc;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_new_game() {
        _new_game_next ??= FhCall.MsSetSaveStartGame.chain_from(h_new_game).fnptr!;
        _new_game_next();
        try {
            state.NewGame(Area);
            log.Info($"Atel loader: new game: {state.Layout.Fields.Count} saved field(s) at their initial values.");
        }
        catch (Exception ex) {
            log.Error($"Atel loader: new game: saved fields: {ex}");
        }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private nint h_write_crc(byte* file) {
        _write_crc_next ??= SaveDataWriteCrc.chain_from(h_write_crc).fnptr!;
        try {
            // Outside the CRC (it covers 0x64b8 bytes of the block), so the file stays valid either way.
            new Span<byte>(file + SaveArea.FileOffset, SaveArea.FileLength - SaveArea.FileOffset).Clear();
        }
        catch (Exception ex) {
            log.Error($"Atel loader: SaveDataWriteCrc hook: {ex}");
        }
        nint result = _write_crc_next(file);
        _written = SaveFingerprint.OfFile(new ReadOnlySpan<byte>(file, SaveArea.FileLength));
        return result;
    }

    /// <summary><c>save_local_state</c>: the state for the save just written.</summary>
    public void Save(FileStream file) {
        try {
            if (_written is null) log.Warning("Atel loader: a save without SaveDataWriteCrc seen; its saved fields' state will not match it on load.");
            StateFile s = state.Capture(Area, _written);
            SavedState.Write(file, s);
            log.Info($"Atel loader: saved fields written for the save ({_written?.ToString() ?? "no fingerprint"}): {state.Layout.Fields.Count} field(s), "
                + $"{state.KeptMods.Count} mod(s) kept unlinked.");
        }
        catch (Exception ex) {
            log.Error($"Atel loader: saving the saved fields: {ex}");
        }
    }

    /// <summary><c>load_local_state</c>: the loaded save's state; <c>g_SaveData</c> holds the save by now.</summary>
    public void Load(FileStream file) {
        _state_loaded = true;
        try {
            StateFile? s = SavedState.Read(file, out string? problem);
            if (problem is not null) log.Warning($"Atel loader: the saved fields' state cannot be read ({problem}); the fields start from their initial values.");
            SaveFingerprint loaded = new(*(ushort*)FhUtil.ptr_at<byte>(SaveFileBuffer + SaveFingerprint.CrcOffset),
                *(uint*)FhUtil.ptr_at<byte>(SaveData + SaveFingerprint.PlayTimeOffset));
            Report(state.Load(Area, s, loaded));
        }
        catch (Exception ex) {
            log.Error($"Atel loader: loading the saved fields: {ex}");
        }
    }

    /// <summary><c>PostLoadGame</c>: a save without a state file (vanilla, or never saved with the loader) gets the initial values.</summary>
    private void PostLoad() {
        if (_state_loaded) {
            _state_loaded = false;
            return;
        }
        try {
            log.Info("Atel loader: the loaded save has no saved fields' state.");
            Report(state.Load(Area, null, null));
        }
        catch (Exception ex) {
            log.Error($"Atel loader: saved fields after the load: {ex}");
        }
    }

    private void Report(System.Collections.Generic.List<(bool Warning, string Text)> report) {
        foreach ((bool warning, string text) in report) {
            if (warning) log.Warning($"Atel loader: {text}");
            else log.Info($"Atel loader: {text}");
        }
    }
}
