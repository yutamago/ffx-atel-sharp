// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace Ffx.Frames;

/// <summary>
///     The dwords the Windows loader rewrites when it loads a PE image away from its preferred base (the base relocation
///     table, <c>IMAGE_DIRECTORY_ENTRY_BASERELOC</c>, type 3 <c>HIGHLOW</c>), as RVAs. Read from an image laid out as the
///     loader maps it (RVA-addressed), through <paramref name="readByte"/>.
/// </summary>
/// <remarks>
///     FFX.exe is <c>DYNAMICBASE</c>: Windows can load it at a different base on each start. Its absolute operands (a
///     global's address, <c>fld dword [0x00b3eb44]</c>) then hold other bytes than the file, and a byte scan without an
///     instruction decoder (<see cref="CodeScan.BranchInto"/>) can read them as a branch. These positions are what it must
///     skip.
/// </remarks>
public static class PeRelocations {
    /// <summary>The relocated dwords' RVAs, sorted; empty when the image has no table or it cannot be read.</summary>
    public static uint[] Read(Func<uint, byte> readByte, uint imageSize) {
        uint U16(uint at) => (uint)(readByte(at) | readByte(at + 1) << 8);
        uint U32(uint at) => U16(at) | U16(at + 2) << 16;
        if (imageSize < 0x400 || U16(0) != 0x5a4d) return [];
        uint nt = U32(0x3c);
        if (nt > imageSize - 0x100 || U32(nt) != 0x00004550) return [];
        uint opt = nt + 24;
        if (U16(opt) != 0x10b || U32(opt + 92) <= 5) return []; // PE32, NumberOfRvaAndSizes
        uint dir = U32(opt + 96 + 5 * 8), size = U32(opt + 96 + 5 * 8 + 4);
        if (dir == 0 || size == 0 || dir > imageSize || size > imageSize - dir) return [];
        List<uint> rvas = [];
        uint p = dir, end = dir + size;
        while (p + 8 <= end) {
            uint page = U32(p), block = U32(p + 4);
            if (block < 8 || block > end - p) break;
            for (uint e = p + 8; e + 2 <= p + block; e += 2) {
                uint entry = U16(e);
                if (entry >> 12 == 3) rvas.Add(page + (entry & 0xfff));
            }
            p += block;
        }
        rvas.Sort();
        return [.. rvas];
    }

    /// <summary>
    ///     For the code at <paramref name="rva"/> of <paramref name="length"/> bytes: which of its bytes the loader may rewrite
    ///     (part of a relocated dword, including one that starts up to 3 bytes before it).
    /// </summary>
    public static bool[] Covered(uint[] sortedRvas, uint rva, int length) {
        bool[] covered = new bool[length];
        int i = Array.BinarySearch(sortedRvas, rva >= 3 ? rva - 3 : 0);
        if (i < 0) i = ~i;
        for (; i < sortedRvas.Length && sortedRvas[i] < rva + (uint)length; i++) {
            long start = (long)sortedRvas[i] - rva;
            for (long k = Math.Max(0, start); k < Math.Min(length, start + 4); k++) covered[k] = true;
        }
        return covered;
    }
}
