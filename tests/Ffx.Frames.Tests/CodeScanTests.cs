// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Frames.Tests;

/// <summary>
///     The checks that decide whether a function can be hooked by overwriting its first 5 bytes. The code here is
///     written for the tests, in the shapes that crashed the game: a loop whose head is the function's second or third
///     instruction.
/// </summary>
public class CodeScanTests {
    private const uint Va = 0x00500000;

    /// <summary>
    ///     <c>push esi; xor esi, esi</c>, then a loop from byte 3: <c>push 0; push esi; call f; add esp, 8; inc esi;
    ///     cmp esi, 2; jl &lt;byte 3&gt;</c>; <c>pop esi; ret</c>.
    /// </summary>
    private static byte[] PadLoop() {
        byte[] code = [
            0x56, 0x33, 0xf6,                   // 0: push esi; xor esi, esi
            0x6a, 0x00,                         // 3: push 0         <- loop head
            0x56,                               // 5: push esi
            0xe8, 0x00, 0x00, 0x00, 0x00,       // 6: call f
            0x83, 0xc4, 0x08,                   // 11: add esp, 8
            0x46,                               // 14: inc esi
            0x83, 0xfe, 0x02,                   // 15: cmp esi, 2
            0x7c, 0x00,                         // 18: jl 3
            0x5e, 0xc3,                         // 20: pop esi; ret
        ];
        code[19] = unchecked((byte)(sbyte)(3 - 20));
        return code;
    }

    [Fact]
    public void A_loop_back_to_byte_3_is_found() {
        byte[] code = PadLoop();
        Assert.Equal(18, CodeScan.PrologueBranch(code, Va));
    }

    [Fact]
    public void The_same_function_without_the_loop_is_safe() {
        byte[] code = PadLoop();
        code[18] = 0x90; // nop
        code[19] = 0x90;
        Assert.Equal(-1, CodeScan.PrologueBranch(code, Va));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_rel32_jcc_into_the_patch_is_found(int into) {
        // push ebp; mov ebp, esp; cmp dword [abs], 0; ...; jne rel32 back.
        byte[] code = new byte[40];
        Array.Fill(code, (byte)0x90);
        int at = 30;
        code[at] = 0x0f;
        code[at + 1] = 0x85;
        BitConverter.GetBytes(into - (at + 6)).CopyTo(code, at + 2);
        Assert.Equal(at, CodeScan.PrologueBranch(code, Va));
    }

    [Fact]
    public void A_jmp_rel32_into_the_patch_is_found() {
        byte[] code = new byte[20];
        Array.Fill(code, (byte)0x90);
        code[10] = 0xe9;
        BitConverter.GetBytes(4 - 15).CopyTo(code, 11);
        Assert.Equal(10, CodeScan.PrologueBranch(code, Va));
    }

    [Theory]
    [InlineData(0)] // a loop back to the function's start runs through the hook again: fine
    [InlineData(5)] // the first byte after the patch is still the game's: fine
    [InlineData(9)]
    public void Branches_outside_bytes_1_to_4_are_safe(int to) {
        byte[] code = new byte[30];
        Array.Fill(code, (byte)0x90);
        code[20] = 0xeb;
        code[21] = unchecked((byte)(sbyte)(to - 22));
        Assert.Equal(-1, CodeScan.PrologueBranch(code, Va));
    }

    [Fact]
    public void Loop_and_jcxz_bytes_are_not_taken_for_branches() {
        // A stray e0 xx (loopne) aimed at byte 1, as found inside other instructions, is ignored.
        byte[] code = new byte[20];
        Array.Fill(code, (byte)0x90);
        code[10] = 0xe0;
        code[11] = unchecked((byte)(sbyte)(1 - 12));
        Assert.Equal(-1, CodeScan.PrologueBranch(code, Va));
    }

    [Fact]
    public void A_short_body_is_read_only_within_its_length() {
        // A 0f 8x at the very end, without its 4 operand bytes, is not read past the body.
        byte[] code = [0x90, 0x90, 0x0f, 0x85, 0xff];
        Assert.Equal(-1, CodeScan.PrologueBranch(code, Va));
    }

    [Fact]
    public void A_call_site_reads_its_target() {
        // call rel32 at 0x0042f5b6 to 0x00822840 (rel32 0x003f3285).
        byte[] code = [0xe8, 0x85, 0x32, 0x3f, 0x00];
        Assert.True(CodeScan.TryCallTarget(code, 0x0042f5b6, out uint target));
        Assert.Equal(0x00822840u, target);
    }

    [Fact]
    public void A_call_backwards_reads_its_target() {
        byte[] code = [0xe8, 0x1b, 0xec, 0xfe, 0xff];
        Assert.True(CodeScan.TryCallTarget(code, 0x0089ae60, out uint target));
        Assert.Equal(0x00889a80u, target);
    }

    [Fact]
    public void Something_else_is_not_a_call_site() {
        Assert.False(CodeScan.TryCallTarget([0xe9, 0, 0, 0, 0], Va, out _));
        Assert.False(CodeScan.TryCallTarget([0xe8, 0, 0, 0], Va, out _));
    }

    [Theory]
    [InlineData(0x0042f5b6u, 0x0d810000u)]
    [InlineData(0x0d810000u, 0x0042f5b6u)]
    [InlineData(0x7ffffff0u, 0x00001000u)]
    [InlineData(0x00001000u, 0xfffff000u)]
    public void A_patched_call_goes_where_it_was_pointed(uint site, uint destination) {
        uint rel = CodeScan.CallRel32(site, destination);
        byte[] code = [0xe8, .. BitConverter.GetBytes(rel)];
        Assert.True(CodeScan.TryCallTarget(code, site, out uint target));
        Assert.Equal(destination, target);
    }

    [Fact]
    public void A_good_chain_has_no_problem() {
        Assert.Null(CodeScan.ChainProblem(0x1000, 0x2000, 0xe9, 0x55, 0x55));
        // Another hook chained over the trampoline (Fahrenheit's own on Sg_MainLoop).
        Assert.Null(CodeScan.ChainProblem(0x1000, 0x2000, 0xe9, 0xe9, 0x55));
    }

    [Fact]
    public void A_missing_chain_is_a_problem() => Assert.NotNull(CodeScan.ChainProblem(0, 0x2000, 0xe9, 0, 0x55));

    [Fact]
    public void A_chain_back_to_the_function_is_a_problem() => Assert.NotNull(CodeScan.ChainProblem(0x2000, 0x2000, 0xe9, 0xe9, 0x55));

    [Fact]
    public void A_function_without_the_hook_jmp_is_a_problem() => Assert.NotNull(CodeScan.ChainProblem(0x1000, 0x2000, 0x55, 0x55, 0x55));

    [Fact]
    public void A_trampoline_that_does_not_start_like_the_function_is_a_problem() => Assert.NotNull(CodeScan.ChainProblem(0x1000, 0x2000, 0xe9, 0xcc, 0x55));
}
