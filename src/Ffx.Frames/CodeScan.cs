// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Frames;

/// <summary>
///     Checks on x86 machine code that decide how a function may be hooked. They only read bytes they are given, so they
///     are tested without the game.
/// </summary>
/// <remarks>
///     <para>
///         Fahrenheit hooks through MinHook: it overwrites a function's first 5 bytes with <c>jmp detour</c>, copies the
///         instructions it overwrote into a trampoline, and the trampoline jumps back after them. A branch inside the
///         function to one of bytes 1 to 4 then lands in the middle of that <c>jmp</c> and runs its address as code.
///         MinHook does not look for such branches. <c>TkUpdateScanControler</c> (its pad loop, <c>jl +3</c>) and
///         <c>updateFFX</c> (its update loop, <c>jne +3</c>) have one, and hooking them crashed the game on its first
///         frame (README, "Limits").
///     </para>
///     <para>Such a function is hooked at its call sites instead (<see cref="CallRel32"/>).</para>
/// </remarks>
internal static class CodeScan {
    /// <summary>The bytes MinHook overwrites at the start of a function: a <c>jmp rel32</c>.</summary>
    public const int PatchSize = 5;

    /// <summary>The first byte of a <c>jmp rel32</c> (MinHook's patch), and of a <c>call rel32</c>.</summary>
    public const byte JmpRel32 = 0xe9, CallRel32Opcode = 0xe8;

    /// <summary>
    ///     The offset in <paramref name="body"/> of the first <c>jmp</c> or <c>jcc</c> (rel8 or rel32) whose target is in
    ///     [<paramref name="from"/>, <paramref name="to"/>), or -1.
    /// </summary>
    /// <param name="body">The function's code.</param>
    /// <param name="bodyVa">The address of <paramref name="body"/>[0].</param>
    /// <param name="from">The first address that must not be a branch target.</param>
    /// <param name="to">The address after the last one.</param>
    /// <remarks>
    ///     There is no instruction decoder: every offset is tried as the start of an instruction. A hit may therefore be
    ///     the bytes of some other instruction (the hook is then refused although it was safe), but a direct branch in
    ///     <paramref name="body"/> is never missed. <c>loop</c> and <c>jcxz</c> (<c>e0</c>..<c>e3</c>) are left out:
    ///     MSVC does not emit them, and as stray bytes they are common. Branches from other functions are not seen.
    /// </remarks>
    public static int BranchInto(ReadOnlySpan<byte> body, uint bodyVa, uint from, uint to) {
        for (int i = 0; i < body.Length; i++) {
            byte op = body[i];
            uint target;
            if ((op is >= 0x70 and <= 0x7f || op == 0xeb) && i + 2 <= body.Length) {
                target = unchecked(bodyVa + (uint)i + 2 + (uint)(sbyte)body[i + 1]);
            }
            else if (op == JmpRel32 && i + 5 <= body.Length) {
                target = unchecked(bodyVa + (uint)i + 5 + (uint)ReadI32(body, i + 1));
            }
            else if (op == 0x0f && i + 6 <= body.Length && body[i + 1] is >= 0x80 and <= 0x8f) {
                target = unchecked(bodyVa + (uint)i + 6 + (uint)ReadI32(body, i + 2));
            }
            else {
                continue;
            }
            if (target >= from && target < to) return i;
        }
        return -1;
    }

    /// <summary>Whether a hook's first 5 bytes are safe to overwrite: no branch in its body goes to bytes 1 to 4.</summary>
    /// <returns>The offset in <paramref name="body"/> of the branch that makes it unsafe, or -1.</returns>
    public static int PrologueBranch(ReadOnlySpan<byte> body, uint va) => BranchInto(body, va, va + 1, va + PatchSize);

    /// <summary>The target of the <c>call rel32</c> at <paramref name="siteVa"/>, if <paramref name="code"/> starts with one.</summary>
    public static bool TryCallTarget(ReadOnlySpan<byte> code, uint siteVa, out uint target) {
        target = 0;
        if (code.Length < 5 || code[0] != CallRel32Opcode) return false;
        target = unchecked(siteVa + 5 + (uint)ReadI32(code, 1));
        return true;
    }

    /// <summary>The <c>rel32</c> that makes the <c>call</c> at <paramref name="siteVa"/> go to <paramref name="destination"/>.</summary>
    public static uint CallRel32(uint siteVa, uint destination) => unchecked(destination - (siteVa + 5));

    /// <summary>
    ///     What is wrong with a hook once Fahrenheit has applied it, or null. <paramref name="next"/> is the address the
    ///     hook's chain calls (a trampoline), <paramref name="original"/> the hooked function.
    /// </summary>
    /// <param name="next">The next link of the hook's chain (0: none).</param>
    /// <param name="original">The hooked function.</param>
    /// <param name="originalFirst">The first byte at <paramref name="original"/> now (MinHook's <c>jmp</c>).</param>
    /// <param name="nextFirst">The first byte at <paramref name="next"/> (not read when it is 0).</param>
    /// <param name="prologueFirst">The function's own first byte, which a trampoline starts with.</param>
    public static string? ChainProblem(nint next, nint original, byte originalFirst, byte nextFirst, byte prologueFirst) {
        if (next == 0) return "Fahrenheit has no next link for it";
        if (next == original) return "its next link is the function itself (it would call its own hook forever)";
        if (originalFirst != JmpRel32) return $"the function does not start with the hook's jmp (0x{originalFirst:x2}): the hook is not in";
        // A trampoline starts with the function's first instruction, or with the jmp of a hook chained over it.
        if (nextFirst != prologueFirst && nextFirst != JmpRel32) return $"its next link does not start like the function (0x{nextFirst:x2}, not 0x{prologueFirst:x2})";
        return null;
    }

    private static int ReadI32(ReadOnlySpan<byte> b, int at) => b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24);
}
