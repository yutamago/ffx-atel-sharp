// SPDX-License-Identifier: MIT

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Cuts;

/// <summary>A named block's <c>jmp</c> in a script block.</summary>
/// <param name="Worker">The worker it is in.</param>
/// <param name="At">The offset of the <c>jmp</c>'s first byte from the block's start (the byte that is <c>b0</c> or <c>f6</c>).</param>
/// <param name="Label">The operand, the label the <c>jmp</c> goes to; the switch keeps it.</param>
/// <param name="LoadedOn">Whether the block was on in the image as it was loaded (<c>f6</c>: a linked mod switched it on).</param>
public sealed record CutSite(int Worker, int At, ushort Label, bool LoadedOn);

/// <summary>What a toggle did to one name's blocks in one image.</summary>
/// <param name="Changed">The bytes written (each a <c>b0</c> to <c>f6</c> or back).</param>
/// <param name="Problem">Why nothing was written, or null: a site whose bytes are not the <c>jmp</c> or <c>nop_76</c> with its operand.</param>
public readonly record struct ToggleResult(int Changed, string? Problem);

/// <summary>
///     The named blocks of one script block, found by signature (<see cref="DeadJumps"/>), and the switch over them:
///     <c>jmp L</c> (<c>b0 lo hi</c>) to <c>nop_76 L</c> (<c>f6 lo hi</c>) and back, the same bytes the linker writes
///     for <c>EnableCut</c> (docs/research/dead-code.md, "Switching a block on").
/// </summary>
public sealed class ScriptCuts {
    private ScriptCuts(string script, IReadOnlyDictionary<string, IReadOnlyList<CutSite>> sites) {
        Script = script;
        Sites = sites;
    }

    /// <summary>The script's name (the block's name string).</summary>
    public string Script { get; }

    /// <summary>The <c>jmp</c>s of each name the block has, by name; names without blocks here are left out.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CutSite>> Sites { get; }

    /// <summary>The block's name string (header <c>+0x0c</c>), or null when the header does not look like a block's.</summary>
    public static string? NameOf(ReadOnlySpan<byte> block) {
        if (BlockHeader.Read(block) is not { } h || h.NameAt >= block.Length) return null;
        ReadOnlySpan<byte> rest = block[(int)h.NameAt..];
        int end = rest[..Math.Min(rest.Length, 64)].IndexOf((byte)0);
        if (end <= 0) return null;
        foreach (byte b in rest[..end]) {
            if (b < 0x20 || b > 0x7e) return null;
        }
        return Encoding.ASCII.GetString(rest[..end]);
    }

    /// <summary>
    ///     Finds the blocks of <paramref name="catalog"/>'s names that <paramref name="script"/> lists in
    ///     <paramref name="block"/>, switched on or not.
    /// </summary>
    /// <exception cref="ScriptFormatException">The block does not parse.</exception>
    public static ScriptCuts Scan(byte[] block, string script, CutCatalog catalog) {
        Dictionary<string, IReadOnlyList<CutSite>> sites = new(StringComparer.Ordinal);
        List<CutInfo> cuts = [.. catalog.In(script)];
        if (cuts.Count > 0) {
            ScriptBlock parsed = ScriptBlock.Parse(block);
            HashSet<string> signatures = [.. cuts.SelectMany(c => c.Signatures)];
            foreach (IGrouping<string, DeadJump> g in DeadJumps.Find(parsed, signatures, switchedOn: true).GroupBy(j => catalog.BySignature[j.Signature].Name)) {
                sites[g.Key] = [.. g.Select(j => new CutSite(j.Worker, parsed.CodeStart + (int)j.Offset, j.Label, j.On))];
            }
        }
        return new ScriptCuts(script, sites);
    }

    /// <summary>
    ///     Switches <paramref name="name"/>'s blocks in <paramref name="image"/> (the loaded block, or a copy): on is
    ///     <c>nop_76</c>, off the <c>jmp</c>, null each block as it was loaded. First checks that every site still holds
    ///     the <c>jmp</c> or the <c>nop_76</c> with its operand; if one does not, writes nothing and says why.
    /// </summary>
    public ToggleResult Toggle(Span<byte> image, string name, bool? on) {
        if (!Sites.TryGetValue(name, out IReadOnlyList<CutSite>? sites)) return new(0, null);
        foreach (CutSite s in sites) {
            if (s.At + 3 > image.Length
                || image[s.At] is not (DeadJumps.JmpByte or DeadJumps.NopByte)
                || BinaryPrimitives.ReadUInt16LittleEndian(image[(s.At + 1)..]) != s.Label) {
                string found = s.At + 3 <= image.Length ? Convert.ToHexStringLower(image.Slice(s.At, 3)) : "past the end";
                return new(0, string.Create(CultureInfo.InvariantCulture,
                    $"{Script} worker {s.Worker} +0x{s.At:x}: expected b0 or f6 then {(byte)s.Label:x2} {(byte)(s.Label >> 8):x2}, found {found}; left alone"));
            }
        }
        int changed = 0;
        foreach (CutSite s in sites) {
            byte want = (on ?? s.LoadedOn) ? DeadJumps.NopByte : DeadJumps.JmpByte;
            if (image[s.At] == want) continue;
            image[s.At] = want;
            changed++;
        }
        return new(changed, null);
    }

    /// <summary>
    ///     The same sites with <see cref="CutSite.LoadedOn"/> taken from <paramref name="original"/> (the opcode byte as
    ///     loaded, by <see cref="CutSite.At"/>) where it has one: for a block that still holds bytes the switch wrote.
    /// </summary>
    public ScriptCuts AsLoaded(IReadOnlyDictionary<int, byte> original) => new(Script, Sites.ToDictionary(kv => kv.Key,
        kv => (IReadOnlyList<CutSite>)[.. kv.Value.Select(s => original.TryGetValue(s.At, out byte b) ? s with { LoadedOn = b == DeadJumps.NopByte } : s)],
        StringComparer.Ordinal));

    /// <summary>How many of <paramref name="name"/>'s blocks are on in <paramref name="image"/> (<c>f6</c> at the site).</summary>
    public int CountOn(ReadOnlySpan<byte> image, string name) {
        int on = 0;
        if (Sites.TryGetValue(name, out IReadOnlyList<CutSite>? sites)) {
            foreach (CutSite s in sites) {
                if (s.At < image.Length && image[s.At] == DeadJumps.NopByte) on++;
            }
        }
        return on;
    }
}

/// <summary>The fields of a block header the switch reads (docs/formats/script-block.md).</summary>
public readonly record struct BlockHeader(uint CodeLength, uint NameAt, uint Length, uint CodeStart) {
    /// <summary>The header's size before the worker table: what is compared to tell a block from the one before at the same address.</summary>
    public const int Size = 0x38;

    /// <summary>The largest block the switch takes (<c>system_01</c> is 161,120 bytes).</summary>
    public const int MaxLength = 16 << 20;

    /// <summary>The header, or null when its offsets do not fit a block (not a script, or memory being set up).</summary>
    public static BlockHeader? Read(ReadOnlySpan<byte> block) {
        if (block.Length < Size) return null;
        BlockHeader h = new(
            BinaryPrimitives.ReadUInt32LittleEndian(block),
            BinaryPrimitives.ReadUInt32LittleEndian(block[0x0c..]),
            BinaryPrimitives.ReadUInt32LittleEndian(block[0x10..]),
            BinaryPrimitives.ReadUInt32LittleEndian(block[0x30..]));
        bool fits = h.Length >= Size && h.Length <= MaxLength
            && h.NameAt >= Size && h.NameAt < h.Length
            && h.CodeStart >= Size && (ulong)h.CodeStart + h.CodeLength <= h.Length;
        return fits ? h : null;
    }
}
