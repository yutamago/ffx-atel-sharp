// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Fahrenheit;

namespace Ffx.Atel.Loader;

/// <summary>
///     Hooks <c>CDfileSize_CD</c> so that a file the loader serves reports its real size there too.
/// </summary>
/// <remarks>
///     <para>
///         The PS2 file layer sizes a file before it reads it, and reads only that many bytes. On PC
///         <c>Cd_QueueRead</c> sizes through <c>CDfileSize_PC</c>, which Fahrenheit makes read the size
///         from disk, except while an Eternal Calm map (393-399) is the graphic map: then it calls
///         <c>CDfileSize_CD</c> (<c>0x0076c320</c>), which takes most sizes from the CD's tables, so a
///         file that grew would be cut at its vanilla length. The map's own <c>.ebp</c> is the exception:
///         for it the game already measures the file on disk (<c>getEternalCalmSceneSize</c>), so this
///         hook changes nothing for the scripts tested so far; it guards every other file read there
///         (docs/research/loader.md).
///     </para>
///     <para>
///         The hook finds the file's path the way <c>Cd_QueueRead</c> does right after it has the size:
///         <c>CDfileName_PC</c> (<c>0x00a42830</c>, same module state), and for an empty entry the
///         built-in name of an Eternal Calm map's <c>.ebp</c> or <c>.mgrp</c>
///         (<see cref="ServedFiles.GamePathOf"/>). For a file in the loader's <c>efl/x</c> it returns that
///         file's size rounded up to 16, as <c>CDfileSize_PC</c> does. Every other file goes to the next
///         hook in the chain (or the original), with the same argument.
///     </para>
/// </remarks>
internal sealed class CdFileSizeHook(ServedFiles served, FhLogger logger) {
    // All cdecl (int): the argument on the stack, the caller pops, the result in eax.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint SizeFn(int number);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint NameFn(int number);

    // Offsets in FFX.exe (image base 0x00400000).
    /// <summary><c>CDfileSize_CD</c> (<c>0x0076c320</c>): file number -> size from the CD's tables.</summary>
    private static FhCallHandle<SizeFn> CDfileSize_CD => new(new FhCallLocation("FFX.exe", 0x0036c320));
    /// <summary><c>CDfileName_PC</c> (<c>0x00a42830</c>): file number -> <c>host0:/ffx/master/…</c>, or NULL for an empty entry.</summary>
    private static FhCallHandle<NameFn> CDfileName_PC => new(new FhCallLocation("FFX.exe", 0x00642830));
    /// <summary><c>getEternalCalmSceneName</c> (<c>0x0067aac0</c>): map -> its built-in <c>.ebp</c> path, NULL unless 393-399.</summary>
    private static FhCallHandle<NameFn> EternalCalmSceneName => new(new FhCallLocation("FFX.exe", 0x0027aac0));
    /// <summary><c>0x0067aa50</c> (table <c>0x00c334d4</c>): map -> its built-in <c>.mgrp</c> path, NULL unless 393-399.</summary>
    private static FhCallHandle<NameFn> EternalCalmMgrpName => new(new FhCallLocation("FFX.exe", 0x0027aa50));

    private readonly ConcurrentDictionary<string, bool> _logged = new(StringComparer.Ordinal);

    private SizeFn? _next;
    private NameFn? _file_name;
    private NameFn? _scene_name;
    private NameFn? _mgrp_name;

    /// <summary>Puts the hook on <c>CDfileSize_CD</c>; false when Fahrenheit could not.</summary>
    public bool Install(FhModule owner) => CDfileSize_CD.hook(owner, h_file_size_cd);

    private static string? text(NameFn fn, int arg) {
        nint p = fn(arg);
        return p == 0 ? null : Marshal.PtrToStringAnsi(p);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private nint h_file_size_cd(int number) {
        _next ??= CDfileSize_CD.chain_from(h_file_size_cd).fnptr!;
        try {
            _file_name  ??= CDfileName_PC.fnptr!;
            _scene_name ??= EternalCalmSceneName.fnptr!;
            _mgrp_name  ??= EternalCalmMgrpName.fnptr!;
            string? path = ServedFiles.GamePathOf(number,
                n => text(_file_name, n), map => text(_scene_name, map), map => text(_mgrp_name, map));
            if (path is not null && served.SizeOf(path) is { } size) {
                if (_logged.TryAdd(path, true)) {
                    logger.Info($"Atel loader: CDfileSize_CD({number}) for {path}: {size} bytes, from the served file");
                }
                return (nint)size;
            }
        }
        catch (Exception ex) {
            // Never let an exception unwind into the game; the vanilla size is the safe answer.
            if (_logged.TryAdd("!", true)) logger.Error($"Atel loader: CDfileSize_CD hook: {ex}");
        }
        return _next(number);
    }
}
