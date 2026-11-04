// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Cuts;

/// <summary>The game's script blocks as the tracker sees them: the ctrl works, their workers' blocks, and the bytes.</summary>
public interface IScriptMemory {
    /// <summary>The number of ctrl works (7 in FFX.exe).</summary>
    int CtrlWorks { get; }

    /// <summary>Whether ctrl work <paramref name="ctrl"/> runs scripts now.</summary>
    bool InUse(int ctrl);

    /// <summary>Adds the block of every worker of ctrl work <paramref name="ctrl"/> to <paramref name="into"/> (repeats allowed).</summary>
    void Blocks(int ctrl, List<nint> into);

    /// <summary>The <paramref name="length"/> bytes of the block at <paramref name="block"/>, writable.</summary>
    Span<byte> Bytes(nint block, int length);
}

/// <summary>Where a name's blocks are loaded now, and how many are on.</summary>
/// <param name="Script">The loaded script.</param>
/// <param name="Ctrl">The ctrl work that runs it.</param>
/// <param name="Blocks">The name's blocks in it.</param>
/// <param name="On">How many of them are on.</param>
/// <param name="LoadedOn">How many were on as loaded (a linked mod's <c>EnableCut</c>).</param>
/// <param name="Problem">Why the switch left them alone, or null.</param>
public sealed record LoadedCut(string Script, int Ctrl, int Blocks, int On, int LoadedOn, string? Problem);

/// <summary>A snapshot for the menu: each name's loaded blocks (no entry: none loaded).</summary>
public sealed record CutStatus(IReadOnlyDictionary<string, IReadOnlyList<LoadedCut>> Loaded) {
    public static CutStatus Empty { get; } = new(new Dictionary<string, IReadOnlyList<LoadedCut>>());
}

/// <summary>
///     Follows the script blocks the game has loaded and keeps each named block the way the player set it
///     (docs/research/dead-code.md, "Switching a block on in game"). Runs on the game thread, once a frame.
/// </summary>
/// <remarks>
///     <para>
///         A block is identified by its name string (header <c>+0x0c</c>, the script's name), so only the scripts that
///         hold named blocks are read. The first time one shows up (a new address, or a different header or name at the same
///         address) its bytes are copied and its named blocks found by signature (<see cref="ScriptCuts.Scan"/>); the
///         result is kept by a hash of the script's code, so loading the same script again costs a hash, not a scan.
///     </para>
///     <para>
///         Every frame it then checks each named block's three bytes and writes the opcode byte where it differs from
///         the switch: so a script the game reads again (a new battle, the map entered again) gets the switches before
///         it runs the blocks. Bytes that are not the expected <c>jmp</c> or <c>nop_76</c> with the block's operand are
///         left alone and reported, and nothing of that name is written in that block.
///     </para>
/// </remarks>
public sealed class CutTracker(CutCatalog catalog, CutStates states, Action<string> info, Action<string> error) {
    private sealed class Loaded(nint at, int ctrl, byte[] header, int length, ScriptCuts cuts) {
        public nint At { get; } = at;
        public int Ctrl { get; } = ctrl;
        public byte[] Header { get; } = header;
        public int Length { get; } = length;
        public ScriptCuts Cuts { get; } = cuts;
        public Dictionary<string, string> Problems { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<nint, Loaded> _loaded = [];
    private readonly HashSet<nint> _ignored = [];            // blocks seen whose name is no listed script, by address
    private readonly Dictionary<nint, byte[]> _ignoredHeaders = [];
    private readonly Dictionary<string, ScriptCuts> _scans = new(StringComparer.Ordinal);
    // The opcode bytes as loaded at the sites this tracker changed, by block and offset: a block seen again at the same
    // address without being read again still has our bytes, and "as loaded" must mean the file's.
    private readonly Dictionary<nint, (byte[] Header, Dictionary<int, byte> Original)> _written = [];
    private readonly List<nint> _blocks = [];
    private bool _dirty = true;

    /// <summary>What is loaded, for the menu; replaced (never changed) when something changes.</summary>
    public CutStatus Status => _status;
    private volatile CutStatus _status = CutStatus.Empty;

    /// <summary>Blocks scanned so far (not counting repeats served from the cache).</summary>
    public int Scans { get; private set; }

    /// <summary>Looks at the loaded blocks and applies the switches. Game thread.</summary>
    public void Tick(IScriptMemory memory) {
        HashSet<nint> present = [];
        for (int k = 0; k < memory.CtrlWorks; k++) {
            if (!memory.InUse(k)) continue;
            _blocks.Clear();
            memory.Blocks(k, _blocks);
            foreach (nint at in _blocks) {
                if (at == 0 || !present.Add(at)) continue;
                See(memory, k, at);
            }
        }
        foreach (nint gone in _loaded.Keys.Where(a => !present.Contains(a)).ToList()) {
            info($"Atel cuts: {_loaded[gone].Cuts.Script} unloaded (ctrl work {_loaded[gone].Ctrl})");
            _loaded.Remove(gone);
            _dirty = true;
        }
        foreach (nint gone in _ignored.Where(a => !present.Contains(a)).ToList()) {
            _ignored.Remove(gone);
            _ignoredHeaders.Remove(gone);
        }
        foreach (Loaded l in _loaded.Values) Apply(memory, l);
        if (_dirty) {
            _status = Snapshot(memory);
            _dirty = false;
        }
    }

    /// <summary>
    ///     What tells a block from the one before it at the same address: the header and the name string (two scripts
    ///     of the same layout differ there at least).
    /// </summary>
    private static byte[] Identity(IScriptMemory memory, nint at) {
        Span<byte> head = memory.Bytes(at, BlockHeader.Size);
        if (BlockHeader.Read(head) is not { } h) return head.ToArray();
        ReadOnlySpan<byte> name = memory.Bytes(at, (int)h.Length)[(int)h.NameAt..];
        int end = name[..Math.Min(name.Length, 64)].IndexOf((byte)0);
        return [.. head, .. name[..(end < 0 ? Math.Min(name.Length, 64) : end)]];
    }

    private void See(IScriptMemory memory, int ctrl, nint at) {
        byte[] head = Identity(memory, at);
        if (_loaded.TryGetValue(at, out Loaded? known)) {
            if (head.SequenceEqual(known.Header)) return;
            _loaded.Remove(at);    // another block at the same address
            _dirty = true;
        }
        if (_ignored.Contains(at) && head.SequenceEqual(_ignoredHeaders[at])) return;
        _ignored.Remove(at);
        _ignoredHeaders.Remove(at);
        if (BlockHeader.Read(head) is not { } h || head.Length == BlockHeader.Size) return;    // not set up yet: look again next frame
        Span<byte> block = memory.Bytes(at, (int)h.Length);
        string? script = ScriptCuts.NameOf(block);
        if (script is null || !catalog.Scripts.Contains(script)) {
            _ignored.Add(at);
            _ignoredHeaders[at] = head;
            return;
        }
        byte[] copy = block.ToArray();
        string key = script + ":" + Convert.ToHexStringLower(SHA256.HashData(copy.AsSpan((int)h.CodeStart, (int)h.CodeLength)));
        if (!_scans.TryGetValue(key, out ScriptCuts? cuts)) {
            try {
                cuts = ScriptCuts.Scan(copy, script, catalog);
            }
            catch (ScriptFormatException ex) {
                error($"Atel cuts: {script} at ctrl work {ctrl}: the loaded block does not parse ({ex.Message}); its switches are off");
                _ignored.Add(at);
                _ignoredHeaders[at] = head;
                return;
            }
            _scans[key] = cuts;
            Scans++;
        }
        // A block this tracker changed and sees again without the game having read it again.
        if (_written.TryGetValue(at, out var mine)) {
            if (mine.Header.AsSpan().SequenceEqual(head)) cuts = cuts.AsLoaded(mine.Original);
            _written.Remove(at);
        }
        Loaded loaded = new(at, ctrl, head, (int)h.Length, cuts);
        _loaded[at] = loaded;
        _dirty = true;
        string found = cuts.Sites.Count == 0 ? "none of its named blocks"
            : string.Join(", ", cuts.Sites.Select(kv => $"Cut.{kv.Key} ({kv.Value.Count} block(s){(kv.Value.Any(s => s.LoadedOn) ? $", {kv.Value.Count(s => s.LoadedOn)} on as loaded" : "")})"));
        info($"Atel cuts: {script} loaded (ctrl work {ctrl}): {found}");
        foreach (CutInfo c in catalog.In(script).Where(c => !cuts.Sites.ContainsKey(c.Name))) {
            loaded.Problems[c.Name] = $"{script}: none of its blocks found (another game version, or a file a mod changed there)";
            error($"Atel cuts: Cut.{c.Name}: {loaded.Problems[c.Name]}");
        }
    }

    private void Apply(IScriptMemory memory, Loaded l) {
        if (l.Cuts.Sites.Count == 0) return;
        Span<byte> image = memory.Bytes(l.At, l.Length);
        foreach ((string name, IReadOnlyList<CutSite> sites) in l.Cuts.Sites) {
            if (l.Problems.ContainsKey(name)) continue;
            bool? want = states.Get(name);
            ToggleResult r = l.Cuts.Toggle(image, name, want);
            if (r.Problem is not null) {
                l.Problems[name] = r.Problem;
                error($"Atel cuts: Cut.{name}: {r.Problem}");
                _dirty = true;
                continue;
            }
            if (r.Changed == 0) continue;
            Remember(l, sites, image);
            _dirty = true;
            info($"Atel cuts: Cut.{name} {(want is null ? "as loaded" : want.Value ? "on" : "off")} in {l.Cuts.Script}: {r.Changed} of {sites.Count} block(s) switched");
        }
    }

    /// <summary>Keeps the loaded byte of each site that now differs from it.</summary>
    private void Remember(Loaded l, IReadOnlyList<CutSite> sites, Span<byte> image) {
        if (!_written.TryGetValue(l.At, out var mine)) _written[l.At] = mine = (l.Header, new Dictionary<int, byte>());
        for (int i = 0; i < sites.Count; i++) {
            byte loaded = sites[i].LoadedOn ? DeadJumps.NopByte : DeadJumps.JmpByte;
            if (image[sites[i].At] == loaded) mine.Original.Remove(sites[i].At);
            else mine.Original.TryAdd(sites[i].At, loaded);
        }
        if (mine.Original.Count == 0) _written.Remove(l.At);
    }

    private CutStatus Snapshot(IScriptMemory memory) {
        Dictionary<string, List<LoadedCut>> by = new(StringComparer.Ordinal);
        foreach (Loaded l in _loaded.Values.OrderBy(l => l.Ctrl).ThenBy(l => l.Cuts.Script, StringComparer.Ordinal)) {
            ReadOnlySpan<byte> image = memory.Bytes(l.At, l.Length);
            foreach (CutInfo c in catalog.In(l.Cuts.Script)) {
                l.Cuts.Sites.TryGetValue(c.Name, out IReadOnlyList<CutSite>? sites);
                if (!by.TryGetValue(c.Name, out List<LoadedCut>? list)) by[c.Name] = list = [];
                list.Add(new LoadedCut(l.Cuts.Script, l.Ctrl, sites?.Count ?? 0, l.Cuts.CountOn(image, c.Name),
                    sites?.Count(s => s.LoadedOn) ?? 0, l.Problems.GetValueOrDefault(c.Name)));
            }
        }
        return new CutStatus(by.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<LoadedCut>)kv.Value, StringComparer.Ordinal));
    }
}
