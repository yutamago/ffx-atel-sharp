// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Fahrenheit;
using Fahrenheit.Atel;
using FhCall = Fahrenheit.FFX.FhCall;

using Ffx.Atel.Linker;

namespace Ffx.Atel.Loader;

/// <summary>
///     The debug natives (<c>Debug.Print</c>, <c>Debug.Break</c>) in native group <c>F</c>, which the game
///     leaves empty (docs/formats/debug.md).
/// </summary>
/// <remarks>
///     <para>
///         <c>AtelInitTotal</c> (<c>0x0086d6d0</c>, once, from <c>TkInit</c>) gives every group 0-15 the
///         default table <c>0x00c52b60</c> through <c>AtelSetUpCallFunc</c> (<c>0x00877880</c>), then registers
///         groups 0, 1, 4-9, b, c, d. The default table has 8 entries whose start returns and whose poll
///         returns 0 ("not done"), so a call into an empty group parks the worker for good, and an index of 8
///         or more reads on into group 1's table. <c>Script_CallNative</c> and the other dispatchers do not
///         check the group. The loader registers its own table for group F after <c>AtelInitTotal</c>: 4,096
///         entries, so every index of the group is defined; those without a native log an error once.
///     </para>
///     <para>
///         An entry is four cdecl handlers (start, poll, float result, int result); the natives here are
///         int-result handlers only, so the call is done at once and the VM pushes what they return
///         (<c>Script_FinishNative</c>, <c>0x008777f0</c>). They pop their arguments with the game's own
///         <c>AtelPopStackInteger</c>/<c>AtelPopStackFloat</c>, choosing by the slot's type.
///     </para>
/// </remarks>
internal static unsafe class DebugNatives {
    /// <summary><c>g_NativeTables</c> (<c>0x01328558</c>): a table pointer per group, as an offset from the image base.</summary>
    private const nint NativeTables = 0x00f28558;

    private const int Entries = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct Entry {
        public nint Start, Poll, ResultFloat, ResultInt;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float PopFloatFn(AtelBasicWorker* work, AtelStack* stack);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InitTotalFn();

    /// <summary><c>AtelPopStackFloat</c> (<c>0x0086de50</c>): the top slot as a float (ST0).</summary>
    private static FhCallHandle<PopFloatFn> AtelPopStackFloat => new(new FhCallLocation("FFX.exe", 0x0046de50));

    /// <summary><c>AtelInitTotal</c> (<c>0x0086d6d0</c>): resets the interpreter and registers the native tables.</summary>
    private static FhCallHandle<InitTotalFn> AtelInitTotal => new(new FhCallLocation("FFX.exe", 0x0046d6d0));

    private static Entry* _table;
    private static FhLogger? _log;
    private static InitTotalFn? _init_total_next;
    private static FhCall.d_AtelPopStackInteger? _pop_int;
    private static PopFloatFn? _pop_float;
    private static readonly ConcurrentDictionary<int, bool> _reported = new();

    /// <summary>Every text the loader was given: from the linked mods and the injected packages.</summary>
    public static DebugStringTable Strings { get; } = new();

    /// <summary>The last lines the natives printed, for the overlay (<see cref="DebugOverlayView"/>).</summary>
    public static DebugLines Lines { get; } = new();

    /// <summary>Whether group F is registered.</summary>
    public static bool Registered { get; private set; }

    /// <summary>
    ///     Builds the table and registers it now, if the game already set its tables up, and on every
    ///     <c>AtelInitTotal</c> (a hook that runs it first).
    /// </summary>
    public static bool Install(FhModule owner, FhLogger log) {
        _log = log;
        if (_table == null) {
            _table = (Entry*)NativeMemory.AllocZeroed((nuint)(Entries * sizeof(Entry)));
            nint unknown = (nint)(delegate* unmanaged[Cdecl]<AtelBasicWorker*, int*, AtelStack*, int>)&Unknown;
            for (int i = 0; i < Entries; i++) _table[i].ResultInt = unknown;
            _table[DebugText.Print & 0xfff].ResultInt = (nint)(delegate* unmanaged[Cdecl]<AtelBasicWorker*, int*, AtelStack*, int>)&Print;
            _table[DebugText.Break & 0xfff].ResultInt = (nint)(delegate* unmanaged[Cdecl]<AtelBasicWorker*, int*, AtelStack*, int>)&Break;
        }
        bool hooked = AtelInitTotal.hook(owner, InitTotalHook);
        // The game's tables are set up once, from TkInit; a loader that starts after that registers now.
        if (FhUtil.get_at<nint>(NativeTables) != 0) Register();
        return hooked || Registered;
    }

    private static void InitTotalHook() {
        _init_total_next ??= AtelInitTotal.chain_from(InitTotalHook).fnptr!;
        _init_total_next();
        Register();
    }

    private static void Register() {
        try {
            FhCall.AtelSetUpCallFunc.fnptr!(DebugText.Group, (nint)_table);
            Registered = FhUtil.get_at<nint>(NativeTables + DebugText.Group * 4) == (nint)_table;
            _log?.Info(Registered
                ? $"Atel loader: debug natives registered as group {DebugText.Group:X} ({Strings.Count} debug text(s) known)."
                : "Atel loader: AtelSetUpCallFunc did not take the debug natives' table.");
        }
        catch (Exception ex) {
            _log?.Error($"Atel loader: debug natives not registered: {ex}");
        }
    }

    /// <summary>Pops one value as its slot's type, the last pushed first.</summary>
    private static object Pop(AtelBasicWorker* work, AtelStack* stack) {
        if (stack->size > 0 && stack->types[stack->size - 1] == AtelStackType.F32) {
            _pop_float ??= AtelPopStackFloat.fnptr!;
            return _pop_float(work, stack);
        }
        _pop_int ??= FhCall.AtelPopStackInteger.fnptr!;
        return _pop_int(work, stack);
    }

    private static int AsInt(object v) => v is float f ? (int)f : (int)v;

    private static string Where(AtelBasicWorker* work) => string.Create(CultureInfo.InvariantCulture, $"worker {work->worker_idx}");

    /// <summary>The line on screen too (the overlay draws it on the render thread).</summary>
    private static void Show(AtelBasicWorker* work, string text) => Lines.Add(new DebugLine(DebugOverlayView.Now, DateTime.Now, work->worker_idx, text));

    /// <summary><c>0xf000</c>: values, their count, the text's id (on top).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Print(AtelBasicWorker* work, int* args, AtelStack* stack) {
        try {
            int id = AsInt(Pop(work, stack));
            int n = Math.Clamp(AsInt(Pop(work, stack)), 0, Math.Min(DebugText.MaxValues, stack->size));
            object[] values = new object[n];
            for (int i = n - 1; i >= 0; i--) values[i] = Pop(work, stack);
            string text = DebugText.Format(Strings.Find(id), id, values);
            _log?.Info($"Atel debug: {Where(work)}: {text}");
            Show(work, text);
        }
        catch (Exception ex) {
            if (_reported.TryAdd(-1, true)) _log?.Error($"Atel loader: Debug.Print: {ex}");
        }
        return 0;
    }

    /// <summary><c>0xf001</c>: the worker's registers to the log, then a .NET debugger if one is attached.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Break(AtelBasicWorker* work, int* args, AtelStack* stack) {
        try {
            List<string> regs = [];
            for (int i = 0; i < 4; i++) regs.Add(string.Create(CultureInfo.InvariantCulture, $"i{i}={work->reg_int[i]}"));
            for (int i = 0; i < 10; i++) regs.Add(string.Create(CultureInfo.InvariantCulture, $"f{i}={work->reg_float[i]}"));
            string text = $"break, stack depth {stack->size}, {string.Join(" ", regs)}";
            _log?.Info($"Atel debug: {Where(work)}: {text}");
            Show(work, text);
            if (Debugger.IsAttached) Debugger.Break();
        }
        catch (Exception ex) {
            if (_reported.TryAdd(-2, true)) _log?.Error($"Atel loader: Debug.Break: {ex}");
        }
        return 0;
    }

    /// <summary>Any other index of group F: nothing is popped (the argument count is unknown); logged once per id.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Unknown(AtelBasicWorker* work, int* args, AtelStack* stack) {
        try {
            if (_reported.TryAdd(0x10000, true)) {
                _log?.Error($"Atel loader: {Where(work)} called a group {DebugText.Group:X} native this loader does not have; its arguments stay on the stack. Is the loader older than the mod?");
            }
        }
        catch {
            // Never unwind into the game.
        }
        return 0;
    }
}
