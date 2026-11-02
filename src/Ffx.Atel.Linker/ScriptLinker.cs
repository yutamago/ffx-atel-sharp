// SPDX-License-Identifier: MIT

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
public sealed record ModScript(string Mod, ScriptPackage Package);

/// <summary>The linked file and what happened.</summary>
public sealed class LinkResult {
    /// <summary>The linked file; the vanilla bytes when nothing applied.</summary>
    public byte[] File { get; set; } = [];

    /// <summary>Whether any package was applied (else <see cref="File"/> is the vanilla file).</summary>
    public bool Changed => Applied.Count > 0;

    /// <summary>The mods whose packages were applied, in load order.</summary>
    public List<string> Applied { get; } = [];

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
    /// <exception cref="ScriptFormatException">The vanilla file is not a script file.</exception>
    public static LinkResult Link(byte[] vanillaFile, IReadOnlyList<ModScript> mods) {
        ScriptFile file = ScriptFile.Parse(vanillaFile);
        ScriptImage vanilla = ScriptImage.FromBlock(file.Script);
        LinkResult result = new() { File = vanillaFile };

        List<ModScript> accepted = [];
        foreach (ModScript mod in mods) {
            List<string> problems = Validate(mod.Package, vanilla);
            if (problems.Count > 0) {
                foreach (string p in problems) result.Messages.Add(new(LinkSeverity.Error, mod.Mod, $"{mod.Package.Script}: {p}; not applied"));
                continue;
            }
            accepted.Add(mod);
        }
        if (accepted.Count == 0) return result;

        State state = new(file.Script, vanilla, result.Messages);
        try {
            state.Apply(accepted);
            byte[] block = state.Write();
            ScriptFile linked = file.ReplaceScript(ScriptBlock.Parse(block));
            result.Applied.AddRange(accepted.Select(m => m.Mod));
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

    // ----- Checks -----------------------------------------------------------------------------

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
            CheckFragment(w.Code, $"worker {w.Name}", false);
        }
        for (int i = 0; i < p.Patches.Count; i++) {
            EntryPatch e = p.Patches[i];
            if (!workers.Contains(e.Worker)) { errors.Add($"{e.Name}: worker {e.Worker} is not in the package's vanilla workers"); continue; }
            if (e.Kind == PatchKind.Entry ? e.Target is not null : !p.Vanilla.Entries.Any(r => r.Worker == e.Worker && r.Name == e.Target)) {
                errors.Add($"{e.Name}: {(e.Kind == PatchKind.Entry ? "a new entry has no target" : $"no vanilla entry {e.Target} on worker {e.Worker}")}");
            }
            CheckFragment(e.Code, e.Name, e.Kind is PatchKind.Prefix or PatchKind.Postfix);
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
    }

    private sealed class State(ScriptBlock block, ScriptImage vanilla, List<LinkMessage> messages) {
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
                    _data.AddRange(new byte[(v.ElementSize * v.Length + 3) & ~3]);
                }
                n.Vars[i] = _vars.Count;
                _vars.Add(new VarSlot(new VarEntry(location, (ushort)v.Length, (ushort)v.RowLength), data));
                messages.Add(new(LinkSeverity.Info, mod.Mod, $"{p.Script}: variable {v.Name} is variable {n.Vars[i]}"));
            }
            n.Workers = new WorkerState[p.Workers.Count];
            for (int i = 0; i < p.Workers.Count; i++) {
                NewWorker nw = p.Workers[i];
                WorkerState w = new() { Type = nw.Type, Word0C = nw.Word0C, CodeChanged = true, Name = nw.Name };
                // Function workers come last in every shipped script that has them; a new worker
                // that is not one goes before them.
                int at = _workers.Count;
                if (nw.Type != 0) {
                    while (at > 0 && _workers[at - 1].Type == 0) at--;
                }
                _workers.Insert(at, w);
                n.Workers[i] = w;
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
            Patch? replace = replaces.LastOrDefault();
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
            if (postfixes.Count == 0) {
                if (replace is not null) Place(replace.Mod, w, replace.Entry.Code);
                else EmitOp(w, Opcodes.Jmp, NewLabel(w, target.Start));
            }
            else {
                int post = NewLabel(w, uint.MaxValue);    // placed after the body
                if (replace is not null) {
                    uint s = Place(replace.Mod, w, replace.Entry.Code);
                    CopyBody(w, s, (uint)w.Code.Count, post, exit, fallThrough: null, where, replace.Mod.Mod.Mod);
                }
                else {
                    CopyBody(w, target.Start, target.End, post, exit, target.End < w.VanillaCodeLength ? target.End : null, where, postfixes[0].Mod.Mod.Mod);
                }
                w.Labels[post] = (uint)w.Code.Count;
                foreach (Patch p in postfixes) Place(p.Mod, w, p.Entry.Code);
                EmitOp(w, exit, null);
            }
            foreach (int e in target.Entries) w.Entries[e] = start;
            w.CodeChanged = true;
            foreach (Patch p in patches) {
                string kind = p.Entry.Kind.ToString().ToLowerInvariant();
                messages.Add(new(LinkSeverity.Info, p.Mod.Mod.Mod, $"{where}: {kind} {p.Entry.Name}"));
            }
        }

        /// <summary>
        ///     Copies the code <paramref name="start"/> to <paramref name="end"/> of the worker to its end,
        ///     with every <paramref name="exit"/> (<c>end</c>, <c>ret</c>) a jump to label
        ///     <paramref name="post"/>, and the jumps within the range to the copy. Code that runs off the
        ///     end goes on at <paramref name="fallThrough"/> (or to the postfixes, when null).
        /// </summary>
        private void CopyBody(WorkerState w, uint start, uint end, int post, byte exit, uint? fallThrough, string where, string mod) {
            List<Instr> code = Decode(w.Code, start, end);
            uint at = (uint)w.Code.Count;
            Dictionary<uint, uint> moved = [];
            foreach (Instr i in code) {
                moved[i.Offset] = at;
                at += i.Code == exit ? 3u : i.Length;
            }
            Dictionary<int, int> labels = [];
            for (int l = 0; l < w.Labels.Count; l++) {
                if (w.Labels[l] >= start && w.Labels[l] < end && moved.TryGetValue(w.Labels[l], out uint to)) labels[l] = NewLabel(w, to);
            }
            int otherExits = 0, outside = 0;
            foreach (Instr i in code) {
                if (i.Code == exit) { EmitOp(w, Opcodes.Jmp, post); continue; }
                if (i.Code is Opcodes.EndP or Opcodes.End1 or Opcodes.EndP1 || (i.Code is Opcodes.End or Opcodes.Ret && i.Code != exit)) otherExits++;
                if (Opcodes.All[i.Code].Operand == OperandKind.Label && i.Operand is ushort l) {
                    if (labels.TryGetValue(l, out int copy)) { EmitOp(w, i.Code, copy); continue; }
                    outside++;
                }
                EmitOp(w, i.Code, i.Operand);
            }
            if (code.Count == 0 || !Unconditional(code[^1].Code)) {
                EmitOp(w, Opcodes.Jmp, fallThrough is uint ft ? NewLabel(w, ft) : post);
            }
            if (otherExits > 0 || outside > 0) {
                messages.Add(new(LinkSeverity.Warning, mod, $"{where}: the postfixes do not run where it ends with endp or end_1 ({otherExits}) or jumps out of its code ({outside})"));
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
                    grown.Contains(w) ? (uint)(start + _private) : null))],
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
