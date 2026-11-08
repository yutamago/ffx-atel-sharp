// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;

namespace Ffx.Frames.Tests;

/// <summary>
///     2026-11-08: the mod refused to run, "Sg_FrameSync (0x00821e80) cannot be hooked: the code at 0x00821ea0 branches into
///     its first 5 bytes". There is no such branch. At <c>0x00821e9d</c> is <c>fld dword [0x00b3eb44]</c>; its operand's
///     bytes <c>44 eb b3 00</c> hold an <c>eb</c> (jmp rel8) at <c>0x00821ea0</c>. In the file its rel8 is <c>b3</c>
///     (to <c>0x00821e55</c>), but FFX.exe is <c>DYNAMICBASE</c>: loaded at a base from 0x6c0000 to 0x700000 the operand's
///     third byte becomes <c>df</c>..<c>e3</c>, and the "jmp" lands on bytes 1 to 4. Whether the mod ran depended on where
///     Windows loaded the game. The scan now skips the bytes the loader rewrites.
/// </summary>
public class RelocationScanTests {
    private const uint FrameSync = 0x00821e80;

    /// <summary>Sg_FrameSync's opening shape: <c>push ebp; mov ebp, esp; sub esp, 0x18</c>, nops, then the <c>fld dword [abs]</c> at +0x1d.</summary>
    private static byte[] FrameSyncShape(uint absolute) {
        byte[] code = new byte[0x30];
        Array.Fill(code, (byte)0x90);
        new byte[] { 0x55, 0x8b, 0xec, 0x83, 0xec, 0x18 }.CopyTo(code, 0);
        code[0x1d] = 0xd9;
        code[0x1e] = 0x05;
        BitConverter.GetBytes(absolute).CopyTo(code, 0x1f);
        return code;
    }

    [Fact]
    public void An_absolute_operand_the_load_base_rewrote_reads_as_a_branch_into_the_patch() {
        // The file's operand: harmless.
        Assert.Equal(-1, CodeScan.PrologueBranch(FrameSyncShape(0x00b3eb44), FrameSync));
        // Loaded at 0x6e0000 (+0x2e0000): 0x00e1eb44, and eb e1 at +0x20 goes to 0x00821e83.
        Assert.Equal(0x20, CodeScan.PrologueBranch(FrameSyncShape(0x00b3eb44 + 0x2e0000), FrameSync));
    }

    [Fact]
    public void Skipping_the_relocated_bytes_finds_nothing_at_any_base() {
        uint rva = FrameSync - 0x00400000;
        bool[] covered = PeRelocations.Covered([rva + 0x1f], rva, 0x30);
        for (uint delta = 0; delta < 0x7f000000; delta += 0x10000) {
            Assert.Equal(-1, CodeScan.PrologueBranch(FrameSyncShape(unchecked(0x00b3eb44 + delta)), FrameSync, covered));
        }
    }

    [Fact]
    public void A_real_loop_back_is_still_found_next_to_a_relocated_operand() {
        byte[] code = FrameSyncShape(0x00e1eb44);
        // jne to byte 3 at +0x28, outside the operand.
        code[0x28] = 0x75;
        code[0x29] = unchecked((byte)(sbyte)(3 - 0x2a));
        uint rva = FrameSync - 0x00400000;
        Assert.Equal(0x28, CodeScan.PrologueBranch(code, FrameSync, PeRelocations.Covered([rva + 0x1f], rva, 0x30)));
    }

    [Fact]
    public void Covered_marks_each_relocated_dword_even_one_starting_before_the_code() {
        bool[] c = PeRelocations.Covered([0x0ff, 0x108, 0x200], 0x100, 0x10);
        Assert.Equal([true, true, true, false, false, false, false, false, true, true, true, true, false, false, false, false], c);
        Assert.All(PeRelocations.Covered([], 0x100, 8), Assert.False);
    }

    [Fact]
    public void The_relocation_table_is_read_from_a_mapped_image() {
        byte[] img = new byte[0x2000];
        img[0] = 0x4d;
        img[1] = 0x5a;
        BitConverter.GetBytes(0x80u).CopyTo(img, 0x3c);
        BitConverter.GetBytes(0x00004550u).CopyTo(img, 0x80);
        uint opt = 0x80 + 24;
        BitConverter.GetBytes((ushort)0x10b).CopyTo(img, (int)opt);
        BitConverter.GetBytes(16u).CopyTo(img, (int)opt + 92);
        BitConverter.GetBytes(0x1000u).CopyTo(img, (int)opt + 96 + 40);
        BitConverter.GetBytes(16u).CopyTo(img, (int)opt + 96 + 44);
        // One block: page 0x421000, entries HIGHLOW 0xe9f, ABSOLUTE (padding), HIGHLOW 0x004, HIGHLOW 0x010.
        BitConverter.GetBytes(0x421000u).CopyTo(img, 0x1000);
        BitConverter.GetBytes(16u).CopyTo(img, 0x1004);
        BitConverter.GetBytes((ushort)0x3e9f).CopyTo(img, 0x1008);
        BitConverter.GetBytes((ushort)0x0000).CopyTo(img, 0x100a);
        BitConverter.GetBytes((ushort)0x3004).CopyTo(img, 0x100c);
        BitConverter.GetBytes((ushort)0x3010).CopyTo(img, 0x100e);
        Assert.Equal([0x421004u, 0x421010u, 0x421e9fu], PeRelocations.Read(a => img[a], (uint)img.Length));
        Assert.Empty(PeRelocations.Read(a => 0, (uint)img.Length));
    }
}

/// <summary>
///     Every hooked function of <see cref="HookSites"/> scanned as <c>CheckGame</c> does, over FFX.exe's own bytes loaded at
///     many bases (its relocations applied). Needs <c>FFX_GAME_DIR</c>; skipped without it.
/// </summary>
public class HookSitesInTheGameTests {
    private sealed class Image {
        public required byte[] Bytes;
        public required uint[] Relocations;
        public byte At(uint rva) => rva < Bytes.Length ? Bytes[rva] : (byte)0;
        public uint U32(uint rva) => BitConverter.ToUInt32(Bytes, (int)rva);
    }

    private static Image? Load() {
        string? dir = Environment.GetEnvironmentVariable("FFX_GAME_DIR");
        if (string.IsNullOrEmpty(dir)) Assert.Skip("FFX_GAME_DIR is not set");
        string exe = Path.Combine(dir, "FFX.exe");
        if (!File.Exists(exe)) Assert.Skip($"no FFX.exe in {dir}");
        byte[] file = File.ReadAllBytes(exe);
        int nt = BitConverter.ToInt32(file, 0x3c);
        int opt = nt + 24;
        uint size = BitConverter.ToUInt32(file, opt + 56), headers = BitConverter.ToUInt32(file, opt + 60);
        byte[] img = new byte[size];
        Array.Copy(file, img, headers);
        int sections = BitConverter.ToUInt16(file, nt + 6), table = opt + BitConverter.ToUInt16(file, nt + 20);
        for (int i = 0; i < sections; i++) {
            int s = table + 40 * i;
            uint va = BitConverter.ToUInt32(file, s + 12), raw = BitConverter.ToUInt32(file, s + 16), at = BitConverter.ToUInt32(file, s + 20);
            Array.Copy(file, at, img, va, Math.Min(raw, size - va));
        }
        return new Image { Bytes = img, Relocations = PeRelocations.Read(a => a < img.Length ? img[a] : (byte)0, size) };
    }

    /// <summary>The function's bytes as the loader leaves them at <c>0x400000 + delta</c>.</summary>
    private static byte[] Loaded(Image img, uint va, int length, uint delta) {
        uint rva = va - 0x00400000;
        byte[] code = new byte[length + 8];
        Array.Copy(img.Bytes, rva - 4, code, 0, length + 8);
        int i = Array.BinarySearch(img.Relocations, rva - 4);
        if (i < 0) i = ~i;
        for (; i < img.Relocations.Length && img.Relocations[i] < rva + length; i++) {
            int at = (int)(img.Relocations[i] - (rva - 4));
            BitConverter.GetBytes(unchecked(BitConverter.ToUInt32(code, at) + delta)).CopyTo(code, at);
        }
        return code[4..(4 + length)];
    }

    private static IEnumerable<uint> Deltas() {
        for (uint b = 0x00010000; b < 0x7fff0000; b += 0x00100000) yield return unchecked(b - 0x00400000);
        yield return unchecked(0x003d0000u - 0x00400000); // the crash dump's base: ran
        yield return 0x006e0000u - 0x00400000;            // a base that refused
    }

    /// <summary>
    ///     The draw counters the mod holds on Redraw frames (<c>Game.ReadDrawCounters</c>: 1 + 1 + 2 bytes, <see cref="DrawCounters"/>): each
    ///     address is the operand the game's own instruction uses, as wide as the mod reads it, and relocated by the loader.
    /// </summary>
    [Theory]
    [InlineData(0x0065b5f0u, 0x00ccba09u, "0f b6 05")] // PhyreScene_drawMainMenuWaveEffect: movzx eax, byte [frame]
    [InlineData(0x0065b6d0u, 0x00ccba0au, "fe 05")]    // inc byte [count]
    [InlineData(0x0063e2bfu, 0x00ccb46cu, "66 a1")]    // graphicDrawDream: mov ax, word [cycle]
    public void The_draw_counters_are_the_game_s_own_operands(uint operand, uint address, string opcode) {
        Image img = Load()!;
        uint rva = operand - 0x00400000;
        Assert.Equal(address, img.U32(rva));
        string[] want = opcode.Split(' ');
        for (int i = 0; i < want.Length; i++) {
            Assert.Equal(Convert.ToByte(want[i], 16), img.At(rva - (uint)want.Length + (uint)i));
        }
        Assert.True(Array.BinarySearch(img.Relocations, rva) >= 0, $"0x{operand:x8} is not relocated");
    }

    [Fact]
    public void FFX_exe_has_relocations() {
        Image img = Load()!;
        Assert.True(img.Relocations.Length > 100000, $"{img.Relocations.Length}");
    }

    [Fact]
    public void Sg_FrameSync_refused_at_base_0x6e0000_without_the_relocations() {
        Image img = Load()!;
        (uint va, _, _, int length) = Array.Find(HookSites.Prologues, p => p.Name == "Sg_FrameSync");
        Assert.Equal(0x20, CodeScan.PrologueBranch(Loaded(img, va, length, 0x002e0000), va));
        Assert.Equal(-1, CodeScan.PrologueBranch(Loaded(img, va, length, unchecked(0x003d0000u - 0x00400000)), va));
    }

    [Fact]
    public void Every_hooked_function_passes_at_every_base() {
        Image img = Load()!;
        List<string> bad = [];
        foreach ((uint va, string name, _, int length) in HookSites.Prologues) {
            bool[] covered = PeRelocations.Covered(img.Relocations, va - 0x00400000, length);
            foreach (uint delta in Deltas()) {
                int at = CodeScan.PrologueBranch(Loaded(img, va, length, delta), va, covered);
                if (at >= 0) bad.Add($"{name} at 0x{va + (uint)at:x8}, base 0x{unchecked(0x00400000 + delta):x8}");
            }
        }
        Assert.Empty(bad);
    }

    [Fact]
    public void No_fixed_byte_of_a_pattern_and_no_hooked_call_is_relocated() {
        Image img = Load()!;
        List<string> bad = [];
        foreach ((uint va, string name, string bytes, _) in HookSites.Prologues) Check(va, name, bytes);
        foreach ((uint va, string name, string bytes, uint[] calls) in HookSites.CallHooked) {
            Check(va, name, bytes);
            foreach (uint site in calls) {
                if (PeRelocations.Covered(img.Relocations, site - 0x00400000, 5) is var c && Array.IndexOf(c, true) >= 0) bad.Add($"call 0x{site:x8}");
                Assert.True(CodeScan.TryCallTarget(img.Bytes.AsSpan((int)(site - 0x00400000), 5), site, out uint target) && target == va, $"0x{site:x8}");
            }
        }
        Assert.Empty(bad);

        void Check(uint va, string name, string bytes) {
            string[] want = bytes.Split(' ');
            bool[] covered = PeRelocations.Covered(img.Relocations, va - 0x00400000, want.Length);
            for (int i = 0; i < want.Length; i++) {
                if (want[i] == "??") continue;
                if (covered[i]) bad.Add($"{name} byte {i} is relocated: make it ??");
                if (img.At(va - 0x00400000 + (uint)i) != Convert.ToByte(want[i], 16)) bad.Add($"{name} byte {i} differs from FFX.exe");
            }
        }
    }
}
