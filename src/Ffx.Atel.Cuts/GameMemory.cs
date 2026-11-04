// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

using Fahrenheit.Atel;

namespace Ffx.Atel.Cuts;

/// <summary>
///     The script blocks in FFX.exe's memory: the 7 ctrl works (<c>g_AtelCtrlWorks</c>, <c>0x01325ba0</c>, <c>0x238</c>
///     bytes each; Fahrenheit's <c>Globals.Atel.controllers</c>), and each worker's block (<c>+0x4</c>, Fahrenheit's
///     <c>AtelBasicWorker.script_chunk</c>): the event's in ctrl work 0, the battle's and each monster's in the battle's.
/// </summary>
/// <remarks>Read and written on the game thread only, between the game's updates.</remarks>
internal sealed unsafe class GameMemory : IScriptMemory {
    private const int Count = 7;
    /// <summary>A ctrl work's worker count (Fahrenheit's <c>runnable_script_count</c>).</summary>
    private const int Workers = 0x0c;
    /// <summary>A ctrl work's worker array.</summary>
    private const int WorkerArray = 0x1c;
    /// <summary>More workers than any script has: past it the ctrl work is not set up.</summary>
    private const int MaxWorkers = 4096;

    public int CtrlWorks => Count;

    private static byte* Ctrl(int k) => (byte*)(Fahrenheit.FFX.Globals.Atel.controllers + k);

    /// <summary>Bit 0 of the ctrl work's first byte: in use (as the loader reads ctrl work 0).</summary>
    public bool InUse(int ctrl) => (Ctrl(ctrl)[0] & 1) != 0;

    public void Blocks(int ctrl, List<nint> into) {
        byte* c = Ctrl(ctrl);
        int n = *(ushort*)(c + Workers);
        if (n == 0 || n > MaxWorkers || *(nint*)(c + WorkerArray) == 0) return;
        AtelWorkerController* controller = (AtelWorkerController*)c;
        for (int i = 0; i < n; i++) {
            AtelBasicWorker* w = controller->worker(i);
            if (w != null) into.Add((nint)w->script_chunk);
        }
    }

    public Span<byte> Bytes(nint block, int length) => new((void*)block, length);
}
