// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Loader;

/// <summary>
///     A place in a running block's code that a worker will go on from: a level's program counter, or a return offset
///     that a <c>call</c> left in the level (docs/formats/debug.md, "Code swaps in memory").
/// </summary>
/// <param name="Offset">The code offset (from the block's code start, <c>+0x30</c>).</param>
/// <param name="Worker">The index in the block of the worker whose work runs it (a function worker's code runs in its caller's work).</param>
/// <param name="Return">A return offset (the instruction after a <c>call</c>), not a program counter.</param>
/// <param name="Where">What it is, for the log: <c>worker 3 level 0</c>.</param>
public readonly record struct LivePosition(uint Offset, int Worker, bool Return, string Where);

/// <summary>Whether a block may be swapped under its running workers, and what that means for them.</summary>
/// <param name="Safe">The swap keeps every running level on consistent code.</param>
/// <param name="Reasons">Why not, when it is not safe.</param>
/// <param name="Notes">What a safe swap leaves as it is (a level that finishes the old version of a method).</param>
public sealed record SwapCheck(bool Safe, IReadOnlyList<string> Reasons, IReadOnlyList<string> Notes);

/// <summary>
///     Swapping a script's block in memory while its workers run: the game-free part (docs/formats/debug.md, "Code swaps
///     in memory"). <see cref="RuntimeInjector"/> finds the block and its workers in the game and applies the result.
/// </summary>
/// <remarks>
///     <para>
///         The swap copies the newly linked block to new memory, with the running block's data in it, and points the
///         block's works at the copy. A level's program counter is an address in the block (level <c>+0x18</c>); its
///         return offsets (<c>+0</c>, three of them) and everything else the interpreter keeps are offsets or indices
///         that it resolves through the block's tables on each use (ffx-rust <c>docs/formats/script.md</c>, "The VM in
///         memory"). So the copy is consistent for a level as long as the code it is in, and every table entry that code
///         names, read the same in both blocks.
///     </para>
///     <para>
///         The linker lays unchanged workers out in place (Phase 9), so the old and the new block share every vanilla
///         byte; a patched method's entry moves to the worker's tail, and its old body stays where it was. A level
///         inside it finishes that old body. A level at the very start of a method has run none of it and takes the new
///         block's code there (a worker's patched first method starts with a <c>jmp</c> to its new code).
///     </para>
/// </remarks>
public static class CodeSwap {
    /// <summary>A worker header's storage offsets: 2, 3 (private data), 4.</summary>
    private const int Storage2 = 0x28, Storage3 = 0x2c, Storage4 = 0x30, PrivateLength = 0x10;

    /// <summary>The block's storage 6 offset (shared by its workers).</summary>
    private const int Storage6 = 0x20;

    /// <summary>The end of a block's code: past its code length when a worker has a tail (code offset, exclusive).</summary>
    public static uint CodeEnd(ScriptBlock block) =>
        Math.Max((uint)block.CodeLength, block.Workers.Count == 0 ? 0 : block.Workers.Max(w => w.HasTail ? w.TailEnd : 0));

    /// <summary>
    ///     Whether <paramref name="memory"/> (a block read from the game, at least the length of <paramref name="block"/>)
    ///     is <paramref name="block"/> as the game runs it: its length, code start, worker count and code bytes are the
    ///     same. The data areas are not compared; the game writes them.
    /// </summary>
    public static bool IsRunning(ScriptBlock block, ReadOnlySpan<byte> memory) {
        ReadOnlySpan<byte> b = block.Bytes;
        if (memory.Length < b.Length) return false;
        if (!memory[..0x38].SequenceEqual(b[..0x38])) return false;
        int start = block.CodeStart, end = start + (int)CodeEnd(block);
        if (end > b.Length) return false;
        return memory[start..end].SequenceEqual(b[start..end]);
    }

    /// <summary>
    ///     Checks a swap from <paramref name="loaded"/> (the block the game runs, as linked) to <paramref name="next"/>
    ///     under <paramref name="live"/>.
    /// </summary>
    /// <param name="loaded">The block the game loaded (or that an earlier swap put in place), as linked.</param>
    /// <param name="next">The block linked now.</param>
    /// <param name="live">Every program counter and return offset of the block's works.</param>
    /// <param name="privateInWorks">
    ///     In battle, storage 3 is a slice of each work (<c>+0xb8</c>) sized once at set-up, so private data must not grow;
    ///     in events it lies in the block (header <c>+0x2c</c>) and may move.
    /// </param>
    public static SwapCheck Check(ScriptBlock loaded, ScriptBlock next, IReadOnlyList<LivePosition> live, bool privateInWorks) {
        List<string> reasons = [], notes = [];
        if (loaded.CodeStart != next.CodeStart) reasons.Add($"the code moved (+0x30 0x{loaded.CodeStart:x} -> 0x{next.CodeStart:x})");
        if (loaded.Workers.Count != next.Workers.Count) {
            reasons.Add($"the worker count changed ({loaded.Workers.Count} -> {next.Workers.Count}); a new worker needs a work the running script was not given");
        }
        if (U32(loaded, Storage6) != U32(next, Storage6)) reasons.Add("the shared data (storage 6) moved");
        if (loaded.Bytes.Length >= 0x38 && next.Bytes.Length >= 0x38 && loaded.U16(0x36) != next.U16(0x36)) reasons.Add("the actor count changed");
        int count = Math.Min(loaded.Workers.Count, next.Workers.Count);
        for (int i = 0; i < count; i++) {
            Worker o = loaded.Workers[i], n = next.Workers[i];
            if (o.Type != n.Type) reasons.Add($"worker {i}'s type changed ({o.Type} -> {n.Type})");
            if (U32(loaded, o.Header + Storage2) != U32(next, n.Header + Storage2)) reasons.Add($"worker {i}'s storage 2 moved");
            if (U32(loaded, o.Header + Storage4) != U32(next, n.Header + Storage4)) reasons.Add($"worker {i}'s storage 4 moved");
            if (privateInWorks && U32(next, n.Header + PrivateLength) > U32(loaded, o.Header + PrivateLength)) {
                reasons.Add($"worker {i}'s private data grew ({U32(loaded, o.Header + PrivateLength)} -> {U32(next, n.Header + PrivateLength)} bytes); in battle each work's slice was sized at set-up");
            }
        }
        if (reasons.Count > 0) return new SwapCheck(false, reasons, notes);
        foreach (LivePosition p in live) {
            if (Position(loaded, next, p, notes) is string why) reasons.Add($"{p.Where}: {why}");
        }
        return new SwapCheck(reasons.Count == 0, reasons, notes);
    }

    /// <summary>Why <paramref name="p"/> cannot go on in <paramref name="next"/>; null when it can.</summary>
    private static string? Position(ScriptBlock loaded, ScriptBlock next, LivePosition p, List<string> notes) {
        // A return offset follows its call: the code it returns into is the call's.
        uint at = p.Return ? p.Offset - 3 : p.Offset;
        if (p.Return && p.Offset < 3) return Hex($"return offset 0x{p.Offset:x} is not after a call");
        int owner = -1;
        for (int i = 0; i < loaded.Workers.Count && owner < 0; i++) {
            if (loaded.Workers[i].Contains(at)) owner = i;
        }
        if (owner < 0) return Hex($"0x{p.Offset:x} is in no worker's code");
        Worker o = loaded.Workers[owner], n = next.Workers[owner];
        // At an entry point it has run nothing of the method yet: the new block's code there starts it.
        if (!p.Return) {
            int e = Array.IndexOf(o.Entries, at);
            if (e >= 0 && e < n.Entries.Length && n.Entries[e] == at) return null;
        }
        (uint lo, uint hi) = Method(o, at);
        int start = loaded.CodeStart;
        if (start + hi > next.Bytes.Length || !loaded.Bytes.Slice(start + (int)lo, (int)(hi - lo)).SequenceEqual(next.Bytes.Slice(start + (int)lo, (int)(hi - lo)))) {
            return Hex($"it is inside worker {owner}'s code 0x{lo:x}-0x{hi:x}, which changed");
        }
        List<Instr> code;
        try {
            code = loaded.Decode(lo, hi);
        }
        catch (ScriptFormatException ex) {
            return $"its code does not decode ({ex.Message})";
        }
        foreach (Instr i in code) {
            if (i.Operand is not ushort op) continue;
            string? changed = i.Opcode.Operand switch {
                OperandKind.Label => Same(o.Labels, n.Labels, op) ? null : $"label {op} of worker {owner}",
                OperandKind.Int => SameInt(loaded, next, owner, p.Worker, op) ? null : $"integer constant {op}",
                OperandKind.Float => SameFloat(loaded, next, owner, p.Worker, op) ? null : $"float constant {op}",
                OperandKind.Var => SameVar(loaded, next, owner, p.Worker, op) ? null : $"variable {op}",
                _ => null,
            };
            if (changed is not null) return Hex($"its code at 0x{i.Offset:x} names {changed}, which changed");
        }
        int moved = Array.IndexOf(o.Entries, lo);
        if (moved >= 0 && (moved >= n.Entries.Length || n.Entries[moved] != lo)) {
            notes.Add(Hex($"{p.Where} finishes the old code of worker {owner}'s entry {moved} (0x{lo:x}); it runs the new one from its next start"));
        }
        return null;
    }

    /// <summary>The method of worker <paramref name="w"/> around <paramref name="at"/>: from the last entry at or before it to the next, within one of its ranges.</summary>
    public static (uint Start, uint End) Method(Worker w, uint at) {
        bool tail = w.HasTail && at >= w.TailStart && at < w.TailEnd;
        (uint first, uint last) = tail ? (w.TailStart, w.TailEnd) : (w.Start, w.End);
        uint lo = first, hi = last;
        foreach (uint e in w.Entries) {
            if (e < first || e >= last) continue;
            if (e <= at && e > lo) lo = e;
            if (e > at && e < hi) hi = e;
        }
        return (lo, hi);
    }

    private static bool Same(uint[] a, uint[] b, int i) => i < a.Length && i < b.Length && a[i] == b[i];

    private static bool SameInt(ScriptBlock a, ScriptBlock b, int owner, int work, ushort i) =>
        a.IntConst(owner, i) == b.IntConst(owner, i) && (work < 0 || work == owner || a.IntConst(work, i) == b.IntConst(work, i));

    private static bool SameFloat(ScriptBlock a, ScriptBlock b, int owner, int work, ushort i) =>
        Equals(a.FloatConst(owner, i), b.FloatConst(owner, i)) && (work < 0 || work == owner || Equals(a.FloatConst(work, i), b.FloatConst(work, i)));

    private static bool SameVar(ScriptBlock a, ScriptBlock b, int owner, int work, ushort i) =>
        a.Variable(owner, i) == b.Variable(owner, i) && (work < 0 || work == owner || a.Variable(work, i) == b.Variable(work, i));

    private static uint U32(ScriptBlock b, int at) => at + 4 <= b.Bytes.Length ? b.U32(at) : 0;

    private static string Hex(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     The block to put in place: <paramref name="next"/>, with the values the game wrote since it loaded
    ///     <paramref name="loaded"/>. A byte where <paramref name="running"/> differs from <paramref name="loaded"/> is
    ///     data the game wrote (code and tables are never written at run time) and is kept, also over a link's data edit
    ///     of that byte (<paramref name="overridden"/> counts those). A worker's private data that the link moved is
    ///     copied to its new place.
    /// </summary>
    /// <param name="kept">How many bytes the game had written and the copy keeps.</param>
    /// <param name="overridden">How many of those the new link had changed too (a data edit of a variable the game set).</param>
    public static byte[] Merge(ScriptBlock loaded, ScriptBlock next, ReadOnlySpan<byte> running, out int kept, out int overridden) {
        byte[] result = next.ToArray();
        ReadOnlySpan<byte> o = loaded.Bytes, n = next.Bytes;
        kept = overridden = 0;
        int limit = Math.Min(Math.Min(o.Length, n.Length), running.Length);
        for (int i = 0; i < limit; i++) {
            if (running[i] == o[i]) continue;
            kept++;
            if (n[i] != o[i]) overridden++;
            result[i] = running[i];
        }
        foreach ((int from, int to, int length) in MovedPrivate(loaded, next)) {
            int len = Math.Min(length, Math.Min(running.Length - from, result.Length - to));
            if (len > 0) running.Slice(from, len).CopyTo(result.AsSpan(to));
        }
        return result;
    }

    /// <summary>The workers' private areas the link moved: old block offset, new block offset, old length.</summary>
    public static IEnumerable<(int From, int To, int Length)> MovedPrivate(ScriptBlock loaded, ScriptBlock next) {
        int count = Math.Min(loaded.Workers.Count, next.Workers.Count);
        for (int i = 0; i < count; i++) {
            uint from = U32(loaded, loaded.Workers[i].Header + Storage3), to = U32(next, next.Workers[i].Header + Storage3);
            uint length = U32(loaded, loaded.Workers[i].Header + PrivateLength);
            if (from != 0 && to != 0 && from != to && length > 0) yield return ((int)from, (int)to, (int)length);
        }
    }

    /// <summary>
    ///     Where a pointer at <paramref name="offset"/> in the old block goes in the new one: the same offset, or, inside a
    ///     private area the link moved, the same place in its new area.
    /// </summary>
    public static int Rebase(ScriptBlock loaded, ScriptBlock next, int offset) {
        foreach ((int from, int to, int length) in MovedPrivate(loaded, next)) {
            if (offset >= from && offset < from + length) return to + (offset - from);
        }
        return offset;
    }

    /// <summary>The size classes of works, in the order they lie (<c>AtelGetBasicWork</c>, <c>0x0086a830</c>).</summary>
    public const int ActorWork = 0xb58, Work558 = 0x558, Work5b8 = 0x5b8, Work2e8 = 0x2e8, SmallWork = 0x30;

    /// <summary>
    ///     The offset of each work from a ctrl work's first (<c>+0x1c</c>) and its size, as <c>AtelGetBasicWork</c>
    ///     (<c>0x0086a830</c>) lays them out: <c>actors - n558 - n2e8 - n5b8</c> of <c>0xb58</c>, then <c>n558</c> of
    ///     <c>0x558</c>, <c>n5b8</c> of <c>0x5b8</c>, <c>n2e8</c> of <c>0x2e8</c>, and the rest of <c>0x30</c>.
    /// </summary>
    /// <param name="actors">Ctrl work <c>+0x0c</c>.</param>
    /// <param name="all">Ctrl work <c>+0x10</c>.</param>
    /// <param name="n5b8">Ctrl work <c>+0x14</c>.</param>
    /// <param name="n558">Ctrl work <c>+0x16</c>.</param>
    /// <param name="n2e8">Ctrl work <c>+0x18</c>.</param>
    public static List<(int Offset, int Size)> Works(int actors, int all, int n5b8, int n558, int n2e8) {
        List<(int, int)> works = [];
        int big = actors - n558 - n2e8 - n5b8, at = 0;
        foreach ((int n, int size) in new[] { (big, ActorWork), (n558, Work558), (n5b8, Work5b8), (n2e8, Work2e8), (all - actors, SmallWork) }) {
            for (int k = 0; k < n && works.Count < all; k++, at += size) works.Add((at, size));
        }
        return works;
    }

    /// <summary>A work's levels (<c>0x4c</c> bytes each from <c>+0x12c</c>): types 5 and 6 have two, the other actors nine, the rest none.</summary>
    public static int Levels(int size, ushort type) => size == SmallWork ? 0 : type is 5 or 6 ? 2 : 9;

    /// <summary>The first level of a work and a level's size; the program counter, call depth and return offsets in a level.</summary>
    public const int LevelsAt = 0x12c, LevelSize = 0x4c, LevelPc = 0x18, LevelDepth = 0x1d;
}
