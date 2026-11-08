// SPDX-License-Identifier: MIT

using System.Globalization;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Linker;

/// <summary>How much a link message matters.</summary>
public enum LinkSeverity {
    /// <summary>What was applied.</summary>
    Info,
    /// <summary>Applied, but something may not work as the mod meant (a postfix that some exits skip).</summary>
    Warning,
    /// <summary>Two mods touch the same thing; says who wins.</summary>
    Conflict,
    /// <summary>The mod's package for this script was not applied (a hash mismatch, a broken package).</summary>
    Error,
}

/// <summary>One line of the link report.</summary>
public sealed record LinkMessage(LinkSeverity Severity, string Mod, string Text) {
    public override string ToString() => $"{Severity.ToString().ToLowerInvariant()}: {Mod}: {Text}";
}

/// <summary>One mod's package for a script, in load order.</summary>
/// <param name="SourceLanguage">The language its new messages are written in (<c>mod.toml</c>'s <c>source_language</c>, an ISO code).</param>
public sealed record ModScript(string Mod, ScriptPackage Package, string SourceLanguage = "en");

/// <summary>A new message (<c>Text.New</c>) and the index the linker gave it in the script's tables.</summary>
public sealed record LinkedMessage(string Mod, int Index, string Text, string SourceLanguage);

/// <summary>The linked file and what happened.</summary>
public sealed class LinkResult {
    /// <summary>The linked file; the vanilla bytes when nothing applied.</summary>
    public byte[] File { get; set; } = [];

    /// <summary>Whether any package was applied (else <see cref="File"/> is the vanilla file).</summary>
    public bool Changed => Applied.Count > 0;

    /// <summary>The mods whose packages were applied, in load order.</summary>
    public List<string> Applied { get; } = [];

    /// <summary>The applied packages' new messages with their indices, in load order; empty when there are none.</summary>
    public List<LinkedMessage> NewMessages { get; } = [];

    public List<LinkMessage> Messages { get; } = [];
}

/// <summary>
///     Links mod packages onto a vanilla script file: checks each package's hashes against the file,
///     applies new variables, workers and entries and the patches on vanilla entries in load order,
///     gives every new thing its number, and lays the block out (docs/formats/mod-package.md).
/// </summary>
/// <remarks>
///     The layout keeps every vanilla byte of the block where it was: the data areas hold block
///     offsets (the "other" area points into the shared data in almost every script), so they must
///     not move. What grows goes after the block's end: new tables, new headers, the new variables'
///     data, and the code. When any code changed, the whole code region is laid out again there, in
///     worker order, and the block's code offset (<c>+0x30</c>) points at it; the old code stays as
///     unused bytes. A file whose script grew past its section shifts its later sections
///     (<see cref="ScriptFile.ReplaceScript"/>).
/// </remarks>
public static class ScriptLinker {
    /// <param name="firstMessage">
    ///     The index the first new message (<c>Text.New</c>) gets: one past the last message of every
    ///     language's table of the script (<see cref="MessageLinker.FirstFree"/>); null when the script has
    ///     no message table, which leaves out a package with new messages.
    /// </param>
    /// <exception cref="ScriptFormatException">The vanilla file is not a script file.</exception>
    public static LinkResult Link(byte[] vanillaFile, IReadOnlyList<ModScript> mods, int? firstMessage = null) {
        ScriptFile file = ScriptFile.Parse(vanillaFile);
        ScriptImage vanilla = ScriptImage.FromBlock(file.Script);
        LinkResult result = new() { File = vanillaFile };

        List<ModScript> accepted = [];
        Dictionary<uint, DeadJump> cutJumps = [];
        foreach (ModScript mod in mods) {
            List<string> problems = Validate(mod.Package, vanilla);
            if (problems.Count == 0) problems.AddRange(ValidateData(mod.Package, file, vanilla));
            if (firstMessage is null && NewMessages(mod.Package).Any()) problems.Add("the script has no message table for Text.New (a monster's messages are the battle's)");
            List<(CutSwitch Cut, List<DeadJump> Jumps)> cuts = problems.Count == 0 ? FindCuts(mod.Package, file, problems) : [];
            if (problems.Count > 0) {
                foreach (string p in problems) result.Messages.Add(new(LinkSeverity.Error, mod.Mod, $"{mod.Package.Script}: {p}; not applied"));
                continue;
            }
            foreach ((CutSwitch cut, List<DeadJump> jumps) in cuts) {
                string where = string.Join(", ", jumps.Select(j => string.Create(CultureInfo.InvariantCulture, $"w{j.Worker:d2} @{j.Offset:x4}")));
                bool again = jumps.All(j => cutJumps.ContainsKey(j.Offset));
                result.Messages.Add(new(LinkSeverity.Info, mod.Mod,
                    $"{mod.Package.Script}: switched on Cut.{cut.Name}, {jumps.Count} block(s): {where}{(again ? " (already on by an earlier mod)" : "")}"));
                foreach (DeadJump j in jumps) cutJumps.TryAdd(j.Offset, j);
            }
            accepted.Add(mod);
        }
        if (accepted.Count == 0) return result;

        if (cutJumps.Count > 0 || accepted.Any(m => m.Package.Data is { Count: > 0 })) {
            // Changes in place, done on the vanilla file before any code is laid out, so the mods' copies of
            // vanilla code (postfix bodies) and of vanilla private data carry them too. A switched-on block: each
            // `jmp L` becomes `nop_76 L`, the same length, so nothing moves. A data edit: its bytes, where the data
            // areas stay (they hold block offsets) or in the monster's sections, which move only as a whole.
            byte[] patched = file.ToArray();
            try {
                foreach (DeadJump j in cutJumps.Values.OrderBy(j => j.Offset)) SwitchOn(patched, file, j);
            }
            catch (LinkException ex) {
                result.Messages.Add(new(LinkSeverity.Error, "", $"{accepted[0].Package.Script}: {ex.Message}; no mod applied to this script"));
                return result;
            }
            ApplyData(patched, file, vanilla, accepted, result.Messages);
            if (accepted.All(m => InPlaceOnly(m.Package))) {
                result.File = patched;
                result.Applied.AddRange(accepted.Select(m => m.Mod));
                return result;
            }
            file = ScriptFile.Parse(patched);
            vanilla = ScriptImage.FromBlock(file.Script);
        }

        State state = new(file.Script, vanilla, result.Messages, firstMessage ?? 0);
        try {
            state.Apply(accepted);
            byte[] block = state.Write();
            ScriptFile linked = file.ReplaceScript(ScriptBlock.Parse(block));
            result.Applied.AddRange(accepted.Select(m => m.Mod));
            result.NewMessages.AddRange(state.NewMessages);
            result.File = linked.ToArray();
            return result;
        }
        catch (LinkException ex) {
            result.Messages.Add(new(LinkSeverity.Error, ex.Mod ?? "", $"{accepted[0].Package.Script}: {ex.Message}; no mod applied to this script"));
            return result;
        }
        catch (ScriptFormatException ex) {
            result.Messages.Add(new(LinkSeverity.Error, "", $"{accepted[0].Package.Script}: the linked block does not parse ({ex.Message}); no mod applied to this script"));
            return result;
        }
    }

    // ----- Cuts (named dead blocks a mod switches on) ------------------------------------------

    /// <summary>
    ///     The blocks of a package's <see cref="ScriptPackage.Cuts"/> in the vanilla file, by signature; a cut
    ///     whose blocks are not all there (another game version, a file another tool changed) is a problem.
    /// </summary>
    private static List<(CutSwitch Cut, List<DeadJump> Jumps)> FindCuts(ScriptPackage p, ScriptFile file, List<string> problems) {
        List<(CutSwitch, List<DeadJump>)> found = [];
        foreach (CutSwitch cut in p.Cuts ?? []) {
            if (cut.Signatures is not { Length: > 0 } || cut.Blocks < 1) {
                problems.Add($"Cut.{cut.Name}: no signatures or no blocks");
                continue;
            }
            List<DeadJump> jumps = DeadJumps.Find(file.Script, cut.Signatures.ToHashSet(StringComparer.Ordinal));
            if (jumps.Count != cut.Blocks) {
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Cut.{cut.Name}: found {jumps.Count} of its {cut.Blocks} dead block(s) (signatures {string.Join(", ", cut.Signatures)}); the script is not the one the mod was built against"));
                continue;
            }
            found.Add((cut, jumps));
        }
        return found;
    }

    /// <summary>
    ///     Turns a dead block's <c>jmp L</c> (<c>b0 lo hi</c>) at <paramref name="j"/> into <c>nop_76 L</c>
    ///     (<c>f6 lo hi</c>) in <paramref name="bytes"/>, a copy of <paramref name="file"/>.
    /// </summary>
    /// <exception cref="LinkException">The three bytes there are not that <c>jmp</c>.</exception>
    private static void SwitchOn(byte[] bytes, ScriptFile file, DeadJump j) {
        int at = file.SectionStart + file.Script.CodeStart + (int)j.Offset;
        if (at + 3 > bytes.Length || bytes[at] != DeadJumps.JmpByte || bytes[at + 1] != (byte)j.Label || bytes[at + 2] != (byte)(j.Label >> 8)) {
            throw new LinkException(string.Create(CultureInfo.InvariantCulture,
                $"the dead block at worker {j.Worker} @{j.Offset:x4} does not start with jmp l{j.Label} (b0 {(byte)j.Label:x2} {(byte)(j.Label >> 8):x2})"));
        }
        bytes[at] = DeadJumps.NopByte;
    }

    // ----- Data edits ---------------------------------------------------------------------------

    /// <summary>What keeps a package's data edits from applying: an area it does not list, a hash mismatch, an edit outside its area.</summary>
    private static List<string> ValidateData(ScriptPackage p, ScriptFile file, ScriptImage vanilla) {
        List<string> errors = [];
        HashSet<(DataEditArea, int?)> listed = [];
        foreach (DataRef d in p.Vanilla.Data ?? []) {
            string what = DataAreas.Describe(d.Area, d.Worker);
            if (DataAreas.Find(file, vanilla, p.File, d.Area, d.Worker) is not DataAreas.Area a) errors.Add($"the script has no {what}");
            else if (VanillaHash.Data(file.Bytes.Slice(a.At, a.Length)) != d.Hash) errors.Add($"{what} differs from the one the mod was built against (hash mismatch)");
            else listed.Add((d.Area, d.Worker));
        }
        if (errors.Count > 0) return errors;
        foreach (DataEdit e in p.Data ?? []) {
            if (!listed.Contains((e.Area, e.Worker))) errors.Add($"{e.Field}: {DataAreas.Describe(e.Area, e.Worker)} is not in the package's vanilla data");
        }
        if (errors.Count == 0) errors.AddRange(DataAreas.Check(p, file, vanilla));
        foreach (NewVariable v in p.Variables) {
            if (v.Initial is not { } init) continue;
            if (v.Offset is not null || v.Storage is not (4 or 6)) errors.Add($"variable {v.Name}: an initial value only for a variable the linker places in the shared or event data");
            else if (init.Length > v.ElementSize * v.Length) errors.Add($"variable {v.Name}: its initial value is longer than the variable");
        }
        return errors;
    }

    /// <summary>
    ///     Writes the accepted packages' data edits into <paramref name="bytes"/> (a copy of <paramref name="file"/>), in
    ///     load order: a byte two mods both set is a conflict, and the later mod's value stays.
    /// </summary>
    private static void ApplyData(byte[] bytes, ScriptFile file, ScriptImage vanilla, List<ModScript> mods, List<LinkMessage> messages) {
        Dictionary<(DataEditArea, int?, int), (string Mod, string Field)> owners = [];
        foreach (ModScript m in mods) {
            foreach (DataEdit e in m.Package.Data ?? []) {
                DataAreas.Area a = DataAreas.Find(file, vanilla, m.Package.File, e.Area, e.Worker)!.Value;
                List<(string Mod, string Field)> earlier = [];
                for (int i = 0; i < e.Bytes.Length; i++) {
                    (DataEditArea, int?, int) key = (e.Area, e.Worker, e.Offset + i);
                    if (owners.TryGetValue(key, out var prev) && prev.Mod != m.Mod && !earlier.Contains(prev)) earlier.Add(prev);
                    owners[key] = (m.Mod, e.Field);
                }
                e.Bytes.CopyTo(bytes, a.At + e.Offset);
                if (e.Area == DataEditArea.MonsterStats) e.Bytes.CopyTo(bytes, DataAreas.StatsCopy(file)!.Value.At + e.Offset);
                string where = string.Create(CultureInfo.InvariantCulture, $"{DataAreas.Describe(e.Area, e.Worker)} +0x{e.Offset:x}");
                messages.Add(new(LinkSeverity.Info, m.Mod, string.Create(CultureInfo.InvariantCulture, $"{m.Package.Script}: sets {e.Field} ({where}, {e.Bytes.Length} byte(s))")));
                foreach ((string mod, string field) in earlier) {
                    messages.Add(new(LinkSeverity.Conflict, m.Mod, $"{m.Package.Script}: {mod} sets {field} and {m.Mod} sets {e.Field}, both at {where}; {m.Mod} wins (last in load order)"));
                }
            }
        }
    }

    /// <summary>
    ///     Whether a package only changes bytes in place (switches blocks on, edits data): then the linked file is the vanilla
    ///     file with those bytes changed.
    /// </summary>
    private static bool InPlaceOnly(ScriptPackage p) =>
        p.Variables.Count == 0 && p.Workers.Count == 0 && p.Patches.Count == 0 && p.Messages is not { Count: > 0 } && p.DebugStrings is not { Count: > 0 };

    // ----- Checks -----------------------------------------------------------------------------

    /// <summary>A package's new messages (<c>Text.New</c>, no index), with their positions in <see cref="ScriptPackage.Messages"/>.</summary>
    public static IEnumerable<(int At, PackageMessage Message)> NewMessages(ScriptPackage p) =>
        (p.Messages ?? []).Select((m, i) => (i, m)).Where(x => x.m.Index is null);

    /// <summary>What keeps a package from applying to this vanilla script; empty when it applies.</summary>
    public static List<string> Validate(ScriptPackage p, ScriptImage vanilla) {
        List<string> errors = [];
        if (p.Format > ScriptPackage.CurrentFormat) {
            errors.Add($"package format {p.Format} is newer than this linker's ({ScriptPackage.CurrentFormat})");
            return errors;
        }
        HashSet<int> workers = [], variables = [];
        foreach (WorkerRef w in p.Vanilla.Workers) {
            if (w.Index < 0 || w.Index >= vanilla.Workers.Count) errors.Add($"worker {w.Index} ({w.Name}) does not exist");
            else if (VanillaHash.Worker(vanilla.Workers[w.Index]) != w.Hash) errors.Add($"worker {w.Index} ({w.Name}) differs from the one the mod was built against (hash mismatch)");
            else workers.Add(w.Index);
        }
        foreach (EntryRef e in p.Vanilla.Entries) {
            if (!workers.Contains(e.Worker)) { errors.Add($"{e.Name} is on worker {e.Worker}, which the package does not list"); continue; }
            WorkerImage w = vanilla.Workers[e.Worker];
            if (e.Entries.Length == 0 || e.Entries.Any(n => n < 0 || n >= w.Entries.Length || w.Entries[n] != e.Start) || e.End > w.Code.Length || e.Start > e.End) {
                errors.Add($"worker {e.Worker}'s {e.Name} is not where the mod expects it");
            }
            else if (VanillaHash.Range(w, e.Start, e.End) != e.Hash) {
                errors.Add($"worker {e.Worker}'s {e.Name} differs from the one the mod was built against (hash mismatch)");
            }
        }
        foreach (VariableRef v in p.Vanilla.Variables) {
            if (v.Index < 0 || v.Index >= vanilla.Variables.Count) errors.Add($"variable {v.Index} ({v.Name}) does not exist");
            else if (VanillaHash.Variable(vanilla.Variables[v.Index]) != v.Hash) errors.Add($"variable {v.Index} ({v.Name}) differs from the one the mod was built against (hash mismatch)");
            else variables.Add(v.Index);
        }
        if (errors.Count > 0) return errors;

        int workerCount = vanilla.Workers.Count;
        foreach (NewVariable v in p.Variables) {
            if (v.Type is < 0 or > 6) errors.Add($"variable {v.Name}: no type {v.Type}");
            if (v.Length is < 1 or > 0xffff || v.RowLength is < 0 or > 0xffff) errors.Add($"variable {v.Name}: length out of range");
            if (v.Storage is < 0 or > 7) errors.Add($"variable {v.Name}: no storage {v.Storage}");
            if (v.Offset is int o && o is < 0 or > 0xff_ffff) errors.Add($"variable {v.Name}: offset out of range");
            if (v.Offset is null && v.Storage == 4 && (vanilla.Shared is null || workerCount == 0)) errors.Add($"variable {v.Name}: the script has no shared data (storage 4)");
            if (v.Offset is null && v.Storage == 6 && vanilla.Event is null) errors.Add($"variable {v.Name}: the script has no event data (storage 6)");
            if (v.Offset is null && v.Storage is not (3 or 4 or 6)) errors.Add($"variable {v.Name}: the linker allocates only in storage 3 (private), 4 (shared) and 6 (event); give an offset");
        }
        if ((p.Workers.Count > 0 || p.Patches.Count > 0) && workerCount == 0) errors.Add("the script has no workers to add code to");
        foreach (NewWorker w in p.Workers) {
            if (w.Type > 6) errors.Add($"worker {w.Name}: no type {w.Type}");
            if (w.Entries.Length == 0) errors.Add($"worker {w.Name} has no entries");
            if (w.Entries.Any(e => e >= w.Code.Bytes.Length)) errors.Add($"worker {w.Name}: an entry past its code");
            if (w.PrivateLength is > 0xff_ffff || (w.Private?.Length ?? 0) > (w.PrivateLength ?? 0)) errors.Add($"worker {w.Name}: its private data does not fit its length");
            if (w.PrivateLength is > 0 && w.Type == 0) errors.Add($"worker {w.Name}: a function worker has no private data (it uses its caller's)");
            CheckFragment(w.Code, $"worker {w.Name}", false);
        }
        for (int i = 0; i < p.Patches.Count; i++) {
            EntryPatch e = p.Patches[i];
            if (!workers.Contains(e.Worker)) { errors.Add($"{e.Name}: worker {e.Worker} is not in the package's vanilla workers"); continue; }
            if (e.Kind == PatchKind.Entry ? e.Target is not null : !p.Vanilla.Entries.Any(r => r.Worker == e.Worker && r.Name == e.Target)) {
                errors.Add($"{e.Name}: {(e.Kind == PatchKind.Entry ? "a new entry has no target" : $"no vanilla entry {e.Target} on worker {e.Worker}")}");
            }
            if (e.Kind == PatchKind.Inject) {
                EntryRef? target = p.Vanilla.Entries.FirstOrDefault(r => r.Worker == e.Worker && r.Name == e.Target);
                if (e.At is not InjectPoint at) errors.Add($"{e.Name}: an inject without its point");
                else if (target is not null && (at.Start >= at.End || at.End > target.End - target.Start)) errors.Add($"{e.Name}: its statement is not inside {e.Target}");
            }
            CheckFragment(e.Code, e.Name, e.Kind is PatchKind.Prefix or PatchKind.Postfix or PatchKind.Inject);
        }
        return errors;

        void CheckFragment(Fragment f, string what, bool runsOn) {
            if (f.Bytes.Length == 0 && !runsOn) errors.Add($"{what}: no code");
            if (f.Labels.Any(l => l > f.Bytes.Length)) errors.Add($"{what}: a label past its code");
            foreach (Reloc r in f.Relocs) {
                if (r.At < 0 || r.At + 3 > f.Bytes.Length || (f.Bytes[r.At] & 0x80) == 0) { errors.Add($"{what}: a relocation at {r.At} is not an instruction with an operand"); continue; }
                bool ok = r.Kind switch {
                    RelocKind.Label => r.Value < f.Labels.Length,
                    RelocKind.Int or RelocKind.Float => true,
                    RelocKind.Var => variables.Contains((int)r.Value),
                    RelocKind.NewVar => r.Value < p.Variables.Count,
                    RelocKind.Worker => workers.Contains((int)r.Value),
                    RelocKind.NewWorker => r.Value < p.Workers.Count,
                    RelocKind.NewEntry => r.Value < p.Patches.Count && p.Patches[(int)r.Value].Kind == PatchKind.Entry,
                    RelocKind.NewMessage => p.Messages is not null && r.Value < p.Messages.Count && p.Messages[(int)r.Value].Index is null,
                    _ => false,
                };
                if (!ok) errors.Add($"{what}: relocation {r.Kind} {r.Value} refers to nothing the package declares");
            }
        }
    }

    // ----- Linking ----------------------------------------------------------------------------

    internal sealed class LinkException(string message, string? mod = null) : Exception(message) {
        public string? Mod { get; } = mod;
    }

    /// <summary>A worker as the link builds it.</summary>
    private sealed class WorkerState {
        public ushort Type;
        public uint Word0C, PrivateLength;
        public int VanillaIndex = -1;
        public int VanillaCodeLength, VanillaEntries, VanillaLabels;
        public List<byte> Code = [];
        public List<uint> Entries = [];
        public List<uint> Labels = [];
        public bool CodeChanged;
        public string Name = "";

        /// <summary>A new worker's private data's initial bytes.</summary>
        public byte[]? NewPrivate;
    }

    /// <summary>
    ///     A variable table entry; <see cref="DataOffset"/> when its storage is allocated in the new data area
    ///     (storage 4, 6) or, for storage 3, in the new private area every worker running its code gets.
    /// </summary>
    private sealed record VarSlot(VarEntry Entry, int? DataOffset);

    /// <summary>What a mod's numbers became.</summary>
    private sealed class ModNumbers {
        public required ModScript Mod;
        public int[] Vars = [];
        public WorkerState[] Workers = [];
        public int[] Entries = [];
        public Dictionary<int, int> Messages = [];
    }

    private sealed class State(ScriptBlock block, ScriptImage vanilla, List<LinkMessage> messages, int firstMessage) {
        public List<LinkedMessage> NewMessages { get; } = [];
        private readonly List<WorkerState> _workers = [];
        private readonly List<VarSlot> _vars = [.. vanilla.Variables.Select(v => new VarSlot(v, null))];
        private readonly List<uint> _ints = [.. vanilla.IntConstants];
        private readonly List<uint> _floats = [.. vanilla.FloatConstants];
        private readonly List<byte> _data = [];
        private int _private;
        private WorkerState[] _byVanilla = [];
        private bool _callsRewritten;

        public void Apply(List<ModScript> mods) {
            for (int w = 0; w < vanilla.Workers.Count; w++) {
                WorkerImage wi = vanilla.Workers[w];
                _workers.Add(new WorkerState {
                    Type = wi.Type, Word0C = wi.Unknown0C, PrivateLength = wi.PrivateLength, VanillaIndex = w,
                    VanillaCodeLength = wi.Code.Length, VanillaEntries = wi.Entries.Length, VanillaLabels = wi.Labels.Length,
                    Code = [.. wi.Code], Entries = [.. wi.Entries], Labels = [.. wi.Labels], Name = $"worker {w}",
                });
            }
            _byVanilla = [.. _workers];

            // Numbers first, so that code can refer to anything any package adds.
            List<ModNumbers> numbers = [.. mods.Select(Assign)];
            RewriteCalls();
            foreach (ModNumbers n in numbers) Emit(n);
            ApplyGroups(numbers);
        }

        private ModNumbers Assign(ModScript mod) {
            ScriptPackage p = mod.Package;
            ModNumbers n = new() { Mod = mod };
            n.Vars = new int[p.Variables.Count];
            for (int i = 0; i < p.Variables.Count; i++) {
                NewVariable v = p.Variables[i];
                uint location = (uint)v.Type << 28 | (uint)v.Storage << 25 | (v.Array ? VarEntry.ArrayBit : 0);
                int? data = null;
                if (v.Offset is int fixedOffset) {
                    location |= (uint)fixedOffset;
                }
                else if (v.Storage == 3) {
                    data = _private;
                    _private += (v.ElementSize * v.Length + 3) & ~3;
                }
                else {
                    while (_data.Count % Math.Max(v.ElementSize, 4) != 0) _data.Add(0);
                    data = _data.Count;
                    byte[] bytes = new byte[(v.ElementSize * v.Length + 3) & ~3];
                    v.Initial?.CopyTo(bytes, 0);
                    _data.AddRange(bytes);
                }
                n.Vars[i] = _vars.Count;
                _vars.Add(new VarSlot(new VarEntry(location, (ushort)v.Length, (ushort)v.RowLength), data));
                messages.Add(new(LinkSeverity.Info, mod.Mod, $"{p.Script}: variable {v.Name} is variable {n.Vars[i]}"));
            }
            n.Workers = new WorkerState[p.Workers.Count];
            for (int i = 0; i < p.Workers.Count; i++) {
                NewWorker nw = p.Workers[i];
                WorkerState w = new() { Type = nw.Type, Word0C = nw.Word0C, CodeChanged = true, Name = nw.Name, PrivateLength = nw.PrivateLength ?? 0, NewPrivate = nw.Private };
                // Function workers come last in every shipped script that has them; a new worker
                // that is not one goes before them.
                int at = _workers.Count;
                if (nw.Type != 0) {
                    while (at > 0 && _workers[at - 1].Type == 0) at--;
                }
                _workers.Insert(at, w);
                n.Workers[i] = w;
            }
            foreach ((int at, PackageMessage m) in ScriptLinker.NewMessages(p)) {
                int index = firstMessage + NewMessages.Count;
                n.Messages[at] = index;
                NewMessages.Add(new LinkedMessage(mod.Mod, index, m.Text, mod.SourceLanguage));
                messages.Add(new(LinkSeverity.Info, mod.Mod, $"{p.Script}: new message {index}: {m.Text}"));
            }
            n.Entries = new int[p.Patches.Count];
            for (int i = 0; i < p.Patches.Count; i++) {
                n.Entries[i] = -1;
                EntryPatch e = p.Patches[i];
                if (e.Kind != PatchKind.Entry) continue;
                WorkerState w = _byVanilla[e.Worker];
                n.Entries[i] = w.Entries.Count;
                w.Entries.Add(0);
                w.CodeChanged = true;
            }
            return n;
        }

        private int Number(WorkerState w) => _workers.IndexOf(w);

        /// <summary>When new workers went before the function workers, the vanilla <c>call</c>s follow them.</summary>
        private void RewriteCalls() {
            if (_byVanilla.Select((w, i) => Number(w) == i).All(same => same)) return;
            foreach (WorkerState w in _byVanilla) {
                foreach (Instr i in Decode(w.Code, 0, (uint)w.VanillaCodeLength)) {
                    if (i.Code != Opcodes.Call || i.Operand is not ushort target || target >= _byVanilla.Length) continue;
                    int now = Number(_byVanilla[target]);
                    if (now == target) continue;
                    Put16(w.Code, (int)i.Offset + 1, now);
                    w.CodeChanged = true;
                    _callsRewritten = true;
                }
            }
            if (_callsRewritten) messages.Add(new(LinkSeverity.Info, "", "new workers went before the function workers; the calls to those were renumbered"));
        }

        private void Emit(ModNumbers n) {
            ScriptPackage p = n.Mod.Package;
            for (int i = 0; i < p.Workers.Count; i++) {
                NewWorker nw = p.Workers[i];
                WorkerState w = n.Workers[i];
                uint start = Place(n, w, nw.Code);
                w.Entries = [.. nw.Entries.Select(e => start + e)];
                messages.Add(new(LinkSeverity.Info, n.Mod.Mod, $"{p.Script}: new worker {nw.Name} is worker {Number(w)}"));
            }
            for (int i = 0; i < p.Patches.Count; i++) {
                EntryPatch e = p.Patches[i];
                if (e.Kind != PatchKind.Entry) continue;
                WorkerState w = _byVanilla[e.Worker];
                w.Entries[n.Entries[i]] = Place(n, w, e.Code);
                messages.Add(new(LinkSeverity.Info, n.Mod.Mod, $"{p.Script}: new entry {e.Name} is entry {n.Entries[i]} of worker {Number(w)}"));
            }
        }

        /// <summary>The patches on one vanilla entry method, from every mod, in load order.</summary>
        private sealed record Patch(ModNumbers Mod, EntryPatch Entry, EntryRef Target);

        private void ApplyGroups(List<ModNumbers> numbers) {
            Dictionary<(int Worker, string Target), List<Patch>> groups = [];
            List<(int, string)> order = [];
            foreach (ModNumbers n in numbers) {
                foreach (EntryPatch e in n.Mod.Package.Patches) {
                    if (e.Kind == PatchKind.Entry) continue;
                    EntryRef target = n.Mod.Package.Vanilla.Entries.First(r => r.Worker == e.Worker && r.Name == e.Target);
                    (int, string) key = (e.Worker, e.Target!);
                    if (!groups.TryGetValue(key, out List<Patch>? list)) {
                        groups[key] = list = [];
                        order.Add(key);
                    }
                    list.Add(new Patch(n, e, target));
                }
            }
            foreach ((int, string) key in order) Chain(groups[key]);
        }

        /// <summary>
        ///     Builds the new code of one entry method: the prefixes in load order, then the body (the
        ///     last replacement, or the vanilla code), then the postfixes in load order, and points its
        ///     entries at it.
        /// </summary>
        private void Chain(List<Patch> patches) {
            EntryRef target = patches[0].Target;
            WorkerState w = _byVanilla[target.Worker];
            string where = $"{patches[0].Mod.Mod.Package.Script}: {w.Name}'s {target.Name}";
            List<Patch> prefixes = [.. patches.Where(p => p.Entry.Kind == PatchKind.Prefix)];
            List<Patch> postfixes = [.. patches.Where(p => p.Entry.Kind == PatchKind.Postfix)];
            List<Patch> replaces = [.. patches.Where(p => p.Entry.Kind == PatchKind.Replace)];
            List<Patch> injects = [.. patches.Where(p => p.Entry.Kind == PatchKind.Inject)];
            Patch? replace = replaces.LastOrDefault();
            if (replace is not null && injects.Count > 0) {
                // An inject points into the vanilla code, which the replacement takes the place of.
                foreach (Patch i in injects) {
                    messages.Add(new(LinkSeverity.Conflict, i.Mod.Mod.Mod,
                        $"{where}: {i.Mod.Mod.Mod}'s inject {i.Entry.Name} ({i.Entry.At!.Anchor}) is not applied: {replace.Mod.Mod.Mod} replaces the method"));
                }
                patches = [.. patches.Where(p => p.Entry.Kind != PatchKind.Inject)];
                injects = [];
            }
            foreach (var same in injects.GroupBy(i => (i.Entry.At!.Position, Point: i.Entry.At.Position == InjectPosition.Before ? i.Entry.At.Start : i.Entry.At.End)).Where(g => g.Select(i => i.Mod).Distinct().Count() > 1)) {
                messages.Add(new(LinkSeverity.Info, same.Last().Mod.Mod.Mod,
                    $"{where}: injects at one point run in load order: {string.Join(", ", same.Select(i => $"{i.Mod.Mod.Mod}'s {i.Entry.Name}"))}"));
            }
            if (replaces.Count > 1) {
                messages.Add(new(LinkSeverity.Conflict, replace!.Mod.Mod.Mod,
                    $"{where} is replaced by {string.Join(", ", replaces.Select(r => r.Mod.Mod.Mod))}; {replace.Mod.Mod.Mod} wins (last in load order)"));
            }
            if (replace is not null && patches.Any(p => p.Entry.Kind != PatchKind.Replace && p.Mod != replace.Mod)) {
                messages.Add(new(LinkSeverity.Info, replace.Mod.Mod.Mod, $"{where}: the other mods' prefixes and postfixes run around {replace.Mod.Mod.Mod}'s replacement"));
            }

            byte exit = w.Type == 0 ? Opcodes.Ret : Opcodes.End;
            uint start = (uint)w.Code.Count;
            foreach (Patch p in prefixes) Place(p.Mod, w, p.Entry.Code);
            if (postfixes.Count == 0 && injects.Count == 0) {
                if (replace is not null) Place(replace.Mod, w, replace.Entry.Code);
                else EmitOp(w, Opcodes.Jmp, NewLabel(w, target.Start));
            }
            else {
                int post = NewLabel(w, uint.MaxValue);    // placed after the body
                if (replace is not null) {
                    uint s = Place(replace.Mod, w, replace.Entry.Code);
                    CopyBody(w, s, (uint)w.Code.Count, post, exit, fallThrough: null, where, replace.Mod.Mod.Mod, [], postfixes.Count > 0);
                }
                else {
                    CopyBody(w, target.Start, target.End, post, exit, target.End < w.VanillaCodeLength ? target.End : null, where,
                        (postfixes.Count > 0 ? postfixes[0] : injects[0]).Mod.Mod.Mod, injects, postfixes.Count > 0);
                }
                w.Labels[post] = (uint)w.Code.Count;
                foreach (Patch p in postfixes) Place(p.Mod, w, p.Entry.Code);
                EmitOp(w, exit, null);
            }
            foreach (int e in target.Entries) w.Entries[e] = start;
            w.CodeChanged = true;
            foreach (Patch p in patches) {
                string kind = p.Entry.Kind.ToString().ToLowerInvariant();
                string at = p.Entry.At is InjectPoint point ? $", {point.Anchor}" : "";
                messages.Add(new(LinkSeverity.Info, p.Mod.Mod.Mod, $"{where}: {kind} {p.Entry.Name}{at}"));
            }
        }

        /// <summary>
        ///     Copies the code <paramref name="start"/> to <paramref name="end"/> of the worker to its end,
        ///     with every <paramref name="exit"/> (<c>end</c>, <c>ret</c>) a jump to label
        ///     <paramref name="post"/>, and the jumps within the range to the copy. Code that runs off the
        ///     end goes on at <paramref name="fallThrough"/> (or to the postfixes, when null). The
        ///     <paramref name="injects"/> go in at their points (<see cref="InjectPoint"/>, offsets from
        ///     <paramref name="start"/>).
        /// </summary>
        /// <remarks>
        ///     At one point, the injects after the statement that ends there run first, then those before the statement that
        ///     starts there, each in load order. Falling into the point runs them all. A jump to the point runs those it
        ///     enters the statement of from outside, or leaves the statement of from inside: an after-inject's when the jump is
        ///     inside its statement (a loop's exit, an <c>if</c>'s end), a before-inject's when the jump is not (a loop's back
        ///     edge to its head stays inside the statement, so the code before it runs once).
        /// </remarks>
        private void CopyBody(WorkerState w, uint start, uint end, int post, byte exit, uint? fallThrough, string where, string mod, List<Patch> injects, bool hasPostfixes) {
            List<Instr> code = Decode(w.Code, start, end);
            // The injects at each point, in the order they run there; and where each one and each instruction goes.
            Dictionary<uint, List<Patch>> at = [];
            foreach (Patch p in injects.Where(p => p.Entry.At!.Position == InjectPosition.After).Concat(injects.Where(p => p.Entry.At!.Position == InjectPosition.Before))) {
                InjectPoint i = p.Entry.At!;
                uint point = start + (i.Position == InjectPosition.Before ? i.Start : i.End);
                if (point != end && code.All(x => x.Offset != point)) throw new LinkException($"{where}: {p.Entry.Name}'s point is not between two instructions", p.Mod.Mod.Mod);
                if (!at.TryGetValue(point, out List<Patch>? list)) at[point] = list = [];
                list.Add(p);
            }
            uint pos = (uint)w.Code.Count;
            Dictionary<uint, uint> moved = [];
            Dictionary<Patch, uint> placed = [];
            void Points(uint offset) {
                foreach (Patch p in at.GetValueOrDefault(offset) ?? []) {
                    placed[p] = pos;
                    pos += (uint)p.Entry.Code.Bytes.Length;
                }
            }
            foreach (Instr i in code) {
                Points(i.Offset);
                moved[i.Offset] = pos;
                pos += i.Code == exit ? 3u : i.Length;
            }
            Points(end);

            // A jump to a point inside the range: past the injects it does not run.
            Dictionary<uint, int> labelAt = [];
            // Null for a jump to the range's end that runs none of the injects there: it leaves the copy.
            int? LabelTo(uint target, uint from) {
                uint? to = moved.TryGetValue(target, out uint t) ? t : null;
                foreach (Patch p in at.GetValueOrDefault(target) ?? []) {
                    InjectPoint i = p.Entry.At!;
                    bool inside = from >= start + i.Start && from < start + i.End;
                    if (i.Position == InjectPosition.After ? inside : !inside) {
                        to = placed[p];
                        break;
                    }
                }
                if (to is not uint u) return null;
                if (!labelAt.TryGetValue(u, out int l)) labelAt[u] = l = NewLabel(w, u);
                return l;
            }

            int otherExits = 0, outside = 0;
            foreach (Instr i in code) {
                PlaceInjects(i.Offset);
                if (i.Code == exit) { EmitOp(w, Opcodes.Jmp, post); continue; }
                if (i.Code is Opcodes.EndP or Opcodes.End1 or Opcodes.EndP1 || (i.Code is Opcodes.End or Opcodes.Ret && i.Code != exit)) otherExits++;
                if (Opcodes.All[i.Code].Operand == OperandKind.Label && i.Operand is ushort l) {
                    if (l < w.Labels.Count && w.Labels[l] >= start && w.Labels[l] <= end && LabelTo(w.Labels[l], i.Offset) is int copy) {
                        EmitOp(w, i.Code, copy);
                        continue;
                    }
                    outside++;
                }
                EmitOp(w, i.Code, i.Operand);
            }
            PlaceInjects(end);
            bool runsOn = code.Count == 0 || !Unconditional(code[^1].Code) || at.ContainsKey(end);
            if (runsOn) EmitOp(w, Opcodes.Jmp, fallThrough is uint ft ? NewLabel(w, ft) : post);
            if (hasPostfixes && (otherExits > 0 || outside > 0)) {
                messages.Add(new(LinkSeverity.Warning, mod, $"{where}: the postfixes do not run where it ends with endp or end_1 ({otherExits}) or jumps out of its code ({outside})"));
            }

            void PlaceInjects(uint offset) {
                foreach (Patch p in at.GetValueOrDefault(offset) ?? []) {
                    if (w.Code.Count != placed[p]) throw new LinkException($"{where}: {p.Entry.Name} is not where it was laid out", p.Mod.Mod.Mod);
                    Place(p.Mod, w, p.Entry.Code);
                }
            }
        }

        private static bool Unconditional(byte op) =>
            op is Opcodes.Jmp or Opcodes.PopAJmp or Opcodes.End or Opcodes.EndP or Opcodes.End1 or Opcodes.EndP1 or Opcodes.Ret;

        private static int NewLabel(WorkerState w, uint at) {
            w.Labels.Add(at);
            return w.Labels.Count - 1;
        }

        private static void EmitOp(WorkerState w, byte op, int? operand) {
            if (operand is int v && v is < 0 or > 0xffff) throw new LinkException($"{w.Name}: operand {v} does not fit 16 bits");
            new Instr(0, op, operand is int o ? (ushort)o : null).Encode(w.Code);
        }

        private static void EmitOp(WorkerState w, byte op, ushort? operand) => new Instr(0, op, operand).Encode(w.Code);

        /// <summary>Appends a fragment to a worker's code with its labels and operands resolved; returns where it starts.</summary>
        private uint Place(ModNumbers n, WorkerState w, Fragment f) {
            uint start = (uint)w.Code.Count;
            int labelBase = w.Labels.Count;
            foreach (uint l in f.Labels) w.Labels.Add(start + l);
            w.Code.AddRange(f.Bytes);
            foreach (Reloc r in f.Relocs) {
                int value = r.Kind switch {
                    RelocKind.Label => labelBase + (int)r.Value,
                    RelocKind.Int => Pooled(_ints, r.Value, "integer"),
                    RelocKind.Float => Pooled(_floats, r.Value, "float"),
                    RelocKind.Var => (int)r.Value,
                    RelocKind.NewVar => n.Vars[r.Value],
                    RelocKind.Worker => Number(_byVanilla[r.Value]),
                    RelocKind.NewWorker => Number(n.Workers[r.Value]),
                    RelocKind.NewEntry => n.Entries[r.Value],
                    RelocKind.NewMessage => n.Messages[(int)r.Value],
                    _ => throw new LinkException($"unknown relocation {r.Kind}", n.Mod.Mod),
                };
                if (value is < 0 or > 0xffff) throw new LinkException($"{w.Name}: {r.Kind} {value} does not fit 16 bits", n.Mod.Mod);
                Put16(w.Code, (int)start + r.At + 1, value);
            }
            w.CodeChanged = true;
            return start;
        }

        private static int Pooled(List<uint> pool, uint value, string kind) {
            int i = pool.IndexOf(value);
            if (i >= 0) return i;
            if (pool.Count >= 0xffff) throw new LinkException($"the {kind} constant pool is full");
            pool.Add(value);
            return pool.Count - 1;
        }

        private static void Put16(List<byte> b, int at, int v) {
            b[at] = (byte)v;
            b[at + 1] = (byte)(v >> 8);
        }

        private static List<Instr> Decode(List<byte> code, uint start, uint end) {
            List<Instr> output = [];
            uint pc = start;
            while (pc < end) {
                byte b = code[(int)pc];
                ushort? operand = null;
                if ((b & 0x80) != 0) {
                    if (pc + 3 > end) throw new LinkException($"an instruction at {pc} runs past the code it is copied from");
                    operand = (ushort)(code[(int)pc + 1] | code[(int)pc + 2] << 8);
                }
                Instr i = new(pc, (byte)(b & 0x7f), operand);
                output.Add(i);
                pc += i.Length;
            }
            return output;
        }

        // ----- Layout -------------------------------------------------------------------------

        /// <summary>
        ///     A worker's code starts at its first entry (every shipped script, and what
        ///     <see cref="ScriptBlock.Parse"/> takes a worker's range to be). When a patch moved the entry
        ///     at the start elsewhere, the code before the first entry still in place goes to the end, with
        ///     a jump back where it ran on into that entry.
        /// </summary>
        private static void StartAtAnEntry(WorkerState w) {
            if (w.Entries.Count == 0) return;
            uint first = w.Entries.Min();
            if (first == 0) return;
            List<Instr> head = Decode(w.Code, 0, first);
            uint length = (uint)w.Code.Count - first;
            uint Move(uint o) => o >= first ? o - first : o + length;
            List<byte> code = [.. w.Code.Skip((int)first), .. w.Code.Take((int)first)];
            w.Code = code;
            for (int i = 0; i < w.Entries.Count; i++) w.Entries[i] = Move(w.Entries[i]);
            for (int i = 0; i < w.Labels.Count; i++) w.Labels[i] = Move(w.Labels[i]);
            if (head.Count > 0 && !Unconditional(head[^1].Code)) EmitOp(w, Opcodes.Jmp, NewLabel(w, 0));
        }

        public byte[] Write() {
            foreach (WorkerState w in _workers) {
                if (w.CodeChanged) StartAtAnEntry(w);
            }
            return WriteBlock();
        }

        private byte[] WriteBlock() {
            (HashSet<WorkerState> grown, int start) = PrivateLayout();
            return BlockWriter.Write(block, vanilla, new BlockWriter.Linked(
                [.. _workers.Select(w => new BlockWriter.LinkedWorker(w.VanillaIndex, w.Type, w.Word0C, w.PrivateLength, [.. w.Code], [.. w.Entries], [.. w.Labels], w.CodeChanged || _callsRewritten,
                    grown.Contains(w) ? (uint)(start + _private) : null, w.NewPrivate))],
                [.. _vars.Select(v => (v.Entry, v.DataOffset))], _ints, _floats, [.. _data], start));
        }

        /// <summary>
        ///     The workers whose private data (storage 3) grows by the new private area, and where the area
        ///     starts in each. Storage 3 is resolved through the worker that runs the code (header
        ///     <c>+0x2c</c>, in battle the work's own buffer sized by <c>+0x10</c>), also inside a function
        ///     worker it calls; so every worker that is not a function worker and runs code using a new
        ///     private variable, its own or a function worker's it calls, gets the area, at one offset past
        ///     the longest private data among them.
        /// </summary>
        private (HashSet<WorkerState> Grown, int Start) PrivateLayout() {
            HashSet<int> vars = [.. Enumerable.Range(0, _vars.Count).Where(i => _vars[i].DataOffset is not null && _vars[i].Entry.Storage == 3)];
            if (vars.Count == 0) return ([], 0);
            Dictionary<WorkerState, bool> uses = [];
            Dictionary<WorkerState, List<int>> calls = [];
            foreach (WorkerState w in _workers) {
                List<int> called = [];
                bool used = false;
                foreach (Instr i in Decode(w.Code, 0, (uint)w.Code.Count)) {
                    if (i.Operand is not ushort operand) continue;
                    if (i.Code == Opcodes.Call) called.Add(operand);
                    else if (Opcodes.All[i.Code].Operand == OperandKind.Var && vars.Contains(operand)) used = true;
                }
                uses[w] = used;
                calls[w] = called;
            }
            for (bool changed = true; changed;) {
                changed = false;
                foreach (WorkerState w in _workers) {
                    if (!uses[w] && calls[w].Any(c => c < _workers.Count && uses[_workers[c]])) changed = uses[w] = true;
                }
            }
            HashSet<WorkerState> grown = [.. _workers.Where(w => uses[w] && w.Type != 0)];
            int start = grown.Count == 0 ? 0 : (int)((grown.Max(w => w.PrivateLength) + 3) & ~3u);
            foreach (WorkerState w in grown) messages.Add(new(LinkSeverity.Info, "", $"{w.Name}'s private data grows to {start + _private} bytes for the mods' hidden locals"));
            return (grown, start);
        }
    }
}
