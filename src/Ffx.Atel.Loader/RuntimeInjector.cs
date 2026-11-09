// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using Fahrenheit;

using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Text;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Linker;

namespace Ffx.Atel.Loader;

/// <summary>
///     Watches the loader's <c>inject/</c> folder while the game runs and applies the packages that appear
///     or change (<see cref="Injection"/>, docs/formats/debug.md "Runtime injection").
/// </summary>
/// <remarks>
///     <para>
///         The file work (reading, linking, encoding) runs on a background thread; game memory is touched only
///         in <see cref="Update"/>, which the module calls before each frame's update on the game thread, when no
///         worker runs.
///     </para>
///     <para>
///         A package's file is rewritten in the loader's <c>efl/x</c> only when Fahrenheit's file loader serves
///         it, i.e. when the file was there at startup: its index is built once. A package for another file
///         takes effect from the next start, when the loader links it with the rest.
///     </para>
///     <para>
///         A script that is running gets the new code at once where that is safe (<see cref="CodeSwap"/>): the block is
///         copied with its running data and its works pointed at the copy. Otherwise, or when the script is not running,
///         the game reads the new file the next time it loads the script.
///     </para>
/// </remarks>
internal sealed unsafe class RuntimeInjector(string loaderDir, string gameDir, string eflDir, IReadOnlyList<AtelMod> linked,
                                             IReadOnlySet<string> served, IReadOnlySet<string> taken, FhLogger log, SavedLayout saved) : IDisposable {
    // Offsets from the image base (FFX.exe at 0x00400000).
    /// <summary>The ctrl works (<c>0x01325ba0</c>, seven of <c>0x238</c> bytes); ctrl work 0 is the event's: <c>+0</c> bit 0 in use, <c>+0x34</c>/<c>+0x38</c> its message tables.</summary>
    private const nint CtrlWork0 = 0x00f25ba0;
    private const int CtrlWorkSize = 0x238, CtrlWorks = 7;
    /// <summary><c>g_EventName</c> (<c>0x023cbc60</c>): the event's folder and name, <c>ka/kami0000</c>.</summary>
    private const nint EventName = 0x01fcbc60;
    /// <summary><c>g_BtlStatus</c> (<c>0x0112a8e0</c>, <c>i8</c>): 0 outside battle, 4-0x22 while it is read and runs.</summary>
    private const nint BtlStatus = 0x00d2a8e0;
    /// <summary><c>g_BtlFileText</c> (<c>0x0112a9b8</c>): the battle's message table, which natives <c>0x7042</c>, <c>0x7072</c>, <c>0x70b7</c>, <c>0x70dc</c> read on every call.</summary>
    private const nint BtlFileText = 0x00d2a9b8;
    /// <summary><c>g_BtlMessages</c> (<c>0x0112a998</c>): <c>system_01</c>'s table, read by <c>0x7120</c>, <c>0x711f</c>, <c>0x7121</c>.</summary>
    private const nint BtlMessages = 0x00d2a998;
    /// <summary><c>g_BtlFileName</c> (<c>0x023112e0</c>): the battle's name, <c>kami00_00</c>.</summary>
    private const nint BtlFileName = 0x01f112e0;
    /// <summary><c>g_AtelManagers</c> (<c>0x01134470</c>): three battle script managers of <c>0x54</c> bytes, a count and eight block pointers at <c>+0x14</c> (ctrl work <c>+0x1f8</c> points at them).</summary>
    private const nint AtelManagers = 0x00d34470;
    private const int ManagerSize = 0x54, Managers = 3;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetLangFn();

    /// <summary><c>TOGetFFXLang</c> (<c>0x006410e0</c>): the language whose <c>new_&lt;lang&gt;pc</c> files the game reads.</summary>
    private static FhCallHandle<GetLangFn> TOGetFFXLang => new(new FhCallLocation("FFX.exe", 0x002410e0));

    /// <summary>How long a swap waits for its workers to leave the code that changed before it leaves the change to the next load.</summary>
    private static readonly TimeSpan SwapWait = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private Dictionary<string, string> _seen = new(StringComparer.Ordinal);
    private volatile object? _lang;       // TOGetFFXLang, boxed, once the game is running
    private volatile bool _full;

    // Messages by (category, script), set by the background thread; Update applies them.
    private volatile Dictionary<(ScriptCategory, string), List<(int Index, byte[] Text)>> _messages = [];
    private int _version;
    private readonly MessageSlot _event = new("event"), _battle = new("battle"), _system = new(BattleTables.SystemScript);

    // Code swaps: every block a file was served or swapped as, and the swaps waiting for the game thread.
    private readonly object _swapLock = new();
    private readonly Dictionary<string, List<byte[]>> _versions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingSwap> _pending = new(StringComparer.Ordinal);

    /// <summary>A swap waiting for the game thread: the new block, and the blocks the game may run now (parsed once).</summary>
    private sealed class PendingSwap(ScriptBlock next, List<ScriptBlock> versions, bool written) {
        public ScriptBlock Next { get; } = next;
        public List<ScriptBlock> Versions { get; } = versions;
        public bool Written { get; } = written;
        public Stopwatch Since { get; } = Stopwatch.StartNew();
        public long LastTry { get; set; } = -1;
        public string? Waiting { get; set; }
    }

    /// <summary>How often a waiting swap looks at the game again.</summary>
    private const int SwapRetryMs = 100;

    public string Folder => Path.Combine(loaderDir, Injection.Folder);

    /// <summary>
    ///     Takes the packages linked at startup as seen, then watches the folder. Their messages are encoded
    ///     once the game runs (its language is known after <c>TkInit</c>).
    /// </summary>
    public void Start(AtelMod startup) {
        lock (_lock) _seen = Snapshot(startup);
        Directory.CreateDirectory(Folder);
        _debounce = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Folder, "*.json") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        FileSystemEventHandler changed = (_, _) => _debounce.Change(300, Timeout.Infinite);
        _watcher.Created += changed;
        _watcher.Changed += changed;
        _watcher.Deleted += changed;
        _watcher.Renamed += (_, _) => _debounce.Change(300, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
        log.Info($"Atel loader: watching {Folder} for runtime injection packages.");
    }

    /// <summary>The packages' JSON by path, to tell which changed.</summary>
    private static Dictionary<string, string> Snapshot(AtelMod mod) =>
        mod.Packages.ToDictionary(kv => kv.Key, kv => kv.Value.ToJson(), StringComparer.Ordinal);

    /// <summary>The folder changed: reads it again and applies what changed. Background thread.</summary>
    private void Reload() {
        lock (_lock) {
            try {
                AtelMod now = Injection.Read(loaderDir);
                foreach (string e in now.Errors) log.Error($"Atel loader: inject: {e}");
                Dictionary<string, string> snapshot = Snapshot(now);
                List<string> changed = [.. snapshot.Where(kv => !_seen.TryGetValue(kv.Key, out string? old) || old != kv.Value).Select(kv => kv.Key)
                    .Concat(_seen.Keys.Where(k => !snapshot.ContainsKey(k)))];
                _seen = snapshot;
                bool full = _full;
                _full = false;
                if (changed.Count > 0 || full) Prepare(now, changed, relink: changed.Count > 0);
            }
            catch (Exception ex) {
                log.Error($"Atel loader: inject: {ex}");
            }
        }
    }

    private void Prepare(AtelMod injected, List<string> changed, bool relink) {
        foreach ((string text, string kept) in DebugNatives.Strings.Add(injected.DebugStrings)) {
            log.Warning($"Atel loader: inject: debug text \"{text}\" has the id of \"{kept}\"; Debug.Print shows the latter");
        }
        VbfArchive? archive = null;
        try {
            Func<string, byte[]?> readVanilla = path => {
                archive ??= GameData.OpenArchive(gameDir);
                return archive.Find(path) is { } entry ? archive.Read(entry) : null;
            };
            if (relink) {
                foreach (string path in changed) Relink(injected, path, readVanilla);
            }
            // Messages, encoded in the game's language.
            Dictionary<(ScriptCategory, string), List<(int, byte[])>> messages = [];
            if (_lang is not int ffxLang) return;     // Update asks again once the game runs
            foreach (ScriptPackage p in injected.Packages.Values.Where(p => p.Messages is { Count: > 0 })) {
                if (GameData.Identify(p.File) is not var (category, name) || category is not (ScriptCategory.Event or ScriptCategory.Battle)) {
                    log.Warning($"Atel loader: inject: {p.Script}: messages are set only in an event's or a battle's table (a monster's texts are its battle's); left out");
                    continue;
                }
                GameLanguage language = Injection.Language(ffxLang) ?? GameLanguage.English;
                archive ??= GameData.OpenArchive(gameDir);
                Charset charset = new ScriptMessages(archive).Charset(language);
                List<(int, byte[])> list = [];
                // New messages (Text.New) at the indices the link gives them, after the enabled mods'.
                List<(int Index, string Text)> texts = [.. p.Messages!.Where(m => m.Index is not null).Select(m => (m.Index!.Value, m.Text))];
                if (ScriptLinker.NewMessages(p).Any()) {
                    string path = p.File.Replace('\\', '/').ToLowerInvariant();
                    LinkResult? relinked = Injection.Relink(linked, injected, path, readVanilla, saved).FirstOrDefault()?.Result;
                    texts.AddRange(relinked?.NewMessages.Where(m => m.Mod == Injection.ModId).Select(m => (m.Index, m.Text)) ?? []);
                }
                foreach ((int index, string text) in texts) {
                    try {
                        list.Add((index, charset.Encode(text)));
                    }
                    catch (TextEncodeException ex) {
                        log.Error($"Atel loader: inject: {p.Script} message {index}: {ex.Message}");
                    }
                }
                if (list.Count > 0) messages[(category, name)] = list;
            }
            _messages = messages;
            Interlocked.Increment(ref _version);
            if (messages.Count > 0) {
                string where = string.Join(", ", messages.Keys.Select(k => k.Item1 == ScriptCategory.Battle ? $"battle {k.Item2}" : k.Item2));
                log.Info($"Atel loader: inject: messages for {where} ({Injection.Language(ffxLang) ?? GameLanguage.English}); set when it runs");
            }
        }
        finally {
            archive?.Dispose();
        }
    }

    private void Relink(AtelMod injected, string path, Func<string, byte[]?> readVanilla) {
        bool isServed = served.Contains(path);
        string target = Path.Combine(eflDir, path.Replace('/', Path.DirectorySeparatorChar));
        // What the game may be running: the file served now (or vanilla, when the loader serves none).
        byte[]? loaded = isServed && File.Exists(target) ? File.ReadAllBytes(target) : readVanilla(path);
        List<LinkedFile> files = Injection.Relink(linked, injected, path, readVanilla, saved);
        byte[]? next = files.Count == 0 ? readVanilla(path) : files[0].Result?.File;
        Remember(path, loaded, readVanilla(path));
        if (!isServed) {
            log.Warning($"Atel loader: inject: {path} was not served at startup, so the game cannot be given a new copy of the file now; it is linked from the next start");
            if (next is not null) Schedule(path, next, written: false);
            return;
        }
        List<LinkMessage> report = [];
        bool ok;
        if (files.Count == 0) {
            // No package left for the file: the vanilla one.
            ok = next is not null;
            if (ok) File.WriteAllBytes(target, next!);
        }
        else {
            ok = ModLoader.Write(files[0], eflDir, taken, report);
            // Its message tables (Text.New): only those served since startup reach the game.
            foreach (LinkedFile table in files.Skip(1)) {
                if (served.Contains(table.Path)) ModLoader.Write(table, eflDir, taken, report);
                else log.Warning($"Atel loader: inject: {table.Path} was not served at startup; its new messages show from the next start");
            }
        }
        foreach (LinkMessage m in report) {
            if (m.Severity is LinkSeverity.Error) log.Error($"Atel loader: inject: {m}");
            else if (m.Severity is LinkSeverity.Conflict or LinkSeverity.Warning) log.Warning($"Atel loader: inject: {m}");
        }
        log.Info(ok
            ? $"Atel loader: inject: {path} linked again; the game reads it the next time it loads the script"
            : $"Atel loader: inject: {path} not written; the served file is unchanged");
        if (ok && next is not null) Schedule(path, next, written: true);
    }

    /// <summary>The block of a script file; null when it is not one.</summary>
    private static ScriptBlock? Block(byte[]? file) {
        if (file is null) return null;
        try {
            ScriptBlock b = ScriptFile.Parse(file).Script;
            int length = (int)Math.Min(b.U32(0x10), (uint)b.Bytes.Length);
            return length == b.Bytes.Length ? b : ScriptBlock.Parse(b.Bytes[..length].ToArray());
        }
        catch (ScriptFormatException) {
            return null;
        }
    }

    /// <summary>Notes the blocks the game may have loaded for <paramref name="path"/>.</summary>
    private void Remember(string path, params byte[]?[] files) {
        foreach (byte[]? f in files) {
            if (Block(f) is ScriptBlock b) Remember(path, b);
        }
    }

    private void Remember(string path, ScriptBlock block) {
        lock (_swapLock) {
            if (!_versions.TryGetValue(path, out List<byte[]>? list)) _versions[path] = list = [];
            if (!list.Any(v => v.AsSpan().SequenceEqual(block.Bytes))) list.Add(block.ToArray());
        }
    }

    private void Schedule(string path, byte[] file, bool written) {
        if (Block(file) is not ScriptBlock b) return;
        lock (_swapLock) {
            List<ScriptBlock> versions = [.. (_versions.GetValueOrDefault(path) ?? []).Select(ScriptBlock.Parse)];
            _pending[path] = new PendingSwap(b, versions, written);
        }
    }

    /// <summary>Sets the running scripts' messages and swaps their code where it changed. Game thread.</summary>
    public void Update() {
        try {
            byte* ctrl = FhUtil.ptr_at<byte>(CtrlWork0);
            sbyte status = *(sbyte*)FhUtil.ptr_at<byte>(BtlStatus);
            bool inEvent = (ctrl[0] & 1) != 0, inBattle = BattleTables.Ready(status);
            if (_lang is null) {
                if (!inEvent && !inBattle) return;
                // An event or a battle runs, so its messages were read in the game's language (TOGetFFXLang answers 0
                // before its manager exists). Encode the packages' messages now.
                _lang = GetLanguage();
                _full = true;
                _debounce?.Change(0, Timeout.Infinite);
                return;
            }
            int version = Volatile.Read(ref _version);
            Dictionary<(ScriptCategory, string), List<(int, byte[])>> all = _messages;
            if (inEvent && Marshal.PtrToStringAnsi((nint)FhUtil.ptr_at<byte>(EventName)) is string eventName) {
                List<(int, byte[])>? mine = all.FirstOrDefault(kv => kv.Key.Item1 == ScriptCategory.Event && Injection.IsEvent(eventName, kv.Key.Item2)).Value;
                Apply(_event, new EventTable(ctrl), version, mine, eventName);
            }
            if (inBattle && Marshal.PtrToStringAnsi((nint)FhUtil.ptr_at<byte>(BtlFileName)) is string battle) {
                all.TryGetValue((ScriptCategory.Battle, battle.ToLowerInvariant()), out List<(int, byte[])>? mine);
                mine ??= all.FirstOrDefault(kv => kv.Key.Item1 == ScriptCategory.Battle && BattleTables.IsBattle(battle, kv.Key.Item2)).Value;
                Apply(_battle, new PointerTable(BtlFileText), version, mine, $"battle {battle}");
            }
            if (status is not (4 or 5 or 6 or 7)) {
                List<(int, byte[])>? mine = all.FirstOrDefault(kv => kv.Key.Item1 == ScriptCategory.Battle && BattleTables.IsBattle(BattleTables.SystemScript, kv.Key.Item2)).Value;
                Apply(_system, new PointerTable(BtlMessages), version, mine, $"battle {BattleTables.SystemScript}");
            }
        }
        catch (Exception ex) {
            log.Error($"Atel loader: inject: messages: {ex}");
        }
        try {
            Swaps();
        }
        catch (Exception ex) {
            log.Error($"Atel loader: inject: code swap: {ex}");
        }
    }

    private void Apply(MessageSlot slot, ITableMemory memory, int version, List<(int, byte[])>? messages, string name) {
        try {
            if (slot.Apply(memory, version, messages, name) is string line) log.Info($"Atel loader: inject: {line}");
        }
        catch (InvalidDataException ex) {
            log.Error($"Atel loader: inject: {ex.Message}");
        }
    }

    private static int GetLanguage() => TOGetFFXLang.fnptr!();

    /// <summary>Ctrl work 0's message table: <c>+0x34</c>, and <c>+0x38</c> where it was the same table.</summary>
    private sealed class EventTable(byte* ctrl) : GameMemory {
        public override nint Get() => *(nint*)(ctrl + 0x34);

        public override void Set(nint table) {
            nint current = *(nint*)(ctrl + 0x34);
            if (*(nint*)(ctrl + 0x38) == current) *(nint*)(ctrl + 0x38) = table;
            *(nint*)(ctrl + 0x34) = table;
        }
    }

    /// <summary>A table pointer at a fixed address (the battle's, <c>system_01</c>'s).</summary>
    private sealed class PointerTable(nint offset) : GameMemory {
        public override nint Get() => *(nint*)FhUtil.ptr_at<byte>(offset);
        public override void Set(nint table) => *(nint*)FhUtil.ptr_at<byte>(offset) = table;
    }

    private abstract class GameMemory : ITableMemory {
        public abstract nint Get();
        public abstract void Set(nint table);
        public ushort U16(nint at) => *(ushort*)at;
        public byte U8(nint at) => *(byte*)at;
        public byte[] Read(nint at, int length) => new ReadOnlySpan<byte>((void*)at, length).ToArray();

        public nint Allocate(byte[] bytes) {
            nint p = (nint)NativeMemory.Alloc((nuint)bytes.Length);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            return p;
        }

        public void Free(nint at) => NativeMemory.Free((void*)at);
    }

    // ---- Code swaps ----

    /// <summary>A work of a block: its address, size, ctrl work and the worker it is (index in the block).</summary>
    private readonly record struct Work(nint At, int Size, int Ctrl, int Worker);

    /// <summary>Tries the waiting swaps: each running copy of the script is swapped when safe. Game thread.</summary>
    private void Swaps() {
        List<KeyValuePair<string, PendingSwap>> pending;
        lock (_swapLock) {
            if (_pending.Count == 0) return;
            pending = [.. _pending];
        }
        foreach ((string path, PendingSwap swap) in pending) {
            long now = swap.Since.ElapsedMilliseconds;
            if (swap.LastTry >= 0 && now - swap.LastTry < SwapRetryMs) continue;
            swap.LastTry = now;
            TrySwap(path, swap, out bool wait);
            if (wait && swap.Since.Elapsed < SwapWait) continue;
            lock (_swapLock) {
                if (_pending.TryGetValue(path, out PendingSwap? current) && current == swap) _pending.Remove(path);
            }
            if (wait) {
                log.Warning($"Atel loader: inject: {path}: not swapped in memory after {SwapWait.TotalSeconds:0} s: {swap.Waiting}; " +
                    (swap.Written ? "the game reads the new code the next time it loads the script" : "the new code comes with the next start"));
            }
        }
    }

    /// <summary>
    ///     Swaps every running copy of <paramref name="path"/>'s block to <paramref name="next"/>; <paramref name="wait"/>
    ///     when a copy is not safe to swap yet. Returns a description of what was swapped, or null.
    /// </summary>
    private string? TrySwap(string path, PendingSwap swap, out bool wait) {
        wait = false;
        ScriptBlock next = swap.Next;
        // Every block a work of an in-use ctrl work runs, with its works.
        Dictionary<nint, List<Work>> blocks = [];
        for (int c = 0; c < CtrlWorks; c++) {
            byte* ctrl = FhUtil.ptr_at<byte>(CtrlWork0 + c * CtrlWorkSize);
            if ((ctrl[0] & 1) == 0) continue;
            nint first = *(nint*)(ctrl + 0x1c);
            if (first == 0) continue;
            List<(int Offset, int Size)> works = CodeSwap.Works(*(ushort*)(ctrl + 0x0c), *(ushort*)(ctrl + 0x10), *(ushort*)(ctrl + 0x14), *(ushort*)(ctrl + 0x16), *(ushort*)(ctrl + 0x18));
            foreach ((int offset, int size) in works) {
                nint w = first + offset;
                nint block = *(nint*)(w + 4);
                if (block == 0) continue;
                if (!blocks.TryGetValue(block, out List<Work>? list)) blocks[block] = list = [];
                list.Add(new Work(w, size, c, -1));
            }
        }
        List<string> swapped = [];
        bool found = false;
        foreach ((nint old, List<Work> works) in blocks) {
            // Already the new block (swapped on an earlier frame)?
            if (*(uint*)(old + 0x10) == next.U32(0x10) && CodeSwap.IsRunning(next, new ReadOnlySpan<byte>((void*)old, next.Bytes.Length))) {
                found = true;
                continue;
            }
            // Which of the file's blocks this is, newest first.
            ScriptBlock? loaded = null;
            for (int v = swap.Versions.Count - 1; v >= 0 && loaded is null; v--) {
                ScriptBlock candidate = swap.Versions[v];
                if (*(uint*)(old + 0x10) != candidate.U32(0x10)) continue;
                if (CodeSwap.IsRunning(candidate, new ReadOnlySpan<byte>((void*)old, candidate.Bytes.Length))) loaded = candidate;
            }
            if (loaded is null) continue;
            found = true;
            List<Work> mine = [.. works.Select(w => w with { Worker = WorkerOf(loaded, *(nint*)w.At - old) })];
            List<LivePosition> live = Live(loaded, old, mine);
            bool inWorks = mine.Any(w => *(nint*)(FhUtil.ptr_at<byte>(CtrlWork0 + w.Ctrl * CtrlWorkSize) + 0x54) != 0);
            SwapCheck check = CodeSwap.Check(loaded, next, live, inWorks);
            if (!check.Safe) {
                string why = string.Join("; ", check.Reasons);
                if (swap.Waiting != why) log.Info($"Atel loader: inject: {path}: waiting to swap the running script: {why}");
                swap.Waiting = why;
                wait = true;
                continue;
            }
            byte[] merged = CodeSwap.Merge(loaded, next, new ReadOnlySpan<byte>((void*)old, loaded.Bytes.Length), out int kept, out int overridden);
            nint copy = (nint)NativeMemory.AlignedAlloc((nuint)merged.Length, 16);
            Marshal.Copy(merged, 0, copy, merged.Length);
            List<string> stale = Repoint(loaded, next, old, copy, mine);
            // The game runs the new block now: a later change starts from it.
            Remember(path, next);
            foreach (string note in check.Notes) log.Info($"Atel loader: inject: {path}: {note}");
            if (overridden > 0) log.Warning($"Atel loader: inject: {path}: {overridden} byte(s) the new link edits were set by the game; the game's values are kept");
            if (stale.Count > 0) log.Warning($"Atel loader: inject: {path}: pointers into the old block left as they are (it stays allocated): {string.Join(", ", stale)}");
            swapped.Add($"{mine.Count} work(s), {kept} byte(s) of running data kept, old block 0x{old:x8} -> 0x{copy:x8} ({merged.Length} bytes)");
        }
        if (!found) {
            log.Info($"Atel loader: inject: {path} is not running; " + (swap.Written ? "the game reads the new code when it loads the script" : "the new code comes with the next start"));
            return null;
        }
        if (swapped.Count == 0) return null;
        string done = string.Join("; ", swapped);
        log.Info($"Atel loader: inject: {path}: swapped in memory: {done}" + (swap.Written ? "" : " (until the script loads again: the file comes with the next start)"));
        return done;
    }

    /// <summary>The worker (index in the block) whose header is at <paramref name="header"/>; -1 for none.</summary>
    private static int WorkerOf(ScriptBlock block, nint header) {
        for (int i = 0; i < block.Workers.Count; i++) {
            if (block.Workers[i].Header == header) return i;
        }
        return -1;
    }

    /// <summary>Every level's program counter in the block's code and its return offsets.</summary>
    private static List<LivePosition> Live(ScriptBlock loaded, nint old, List<Work> works) {
        List<LivePosition> live = [];
        nint code = old + loaded.CodeStart, end = code + (nint)CodeSwap.CodeEnd(loaded);
        foreach (Work w in works) {
            ushort type = w.Worker >= 0 ? loaded.Workers[w.Worker].Type : (ushort)0;
            for (int k = 0; k < CodeSwap.Levels(w.Size, type); k++) {
                nint l = w.At + CodeSwap.LevelsAt + k * CodeSwap.LevelSize;
                nint pc = *(nint*)(l + CodeSwap.LevelPc);
                string where = $"worker {w.Worker} (ctrl work {w.Ctrl}) level {k}";
                if (pc >= code && pc < end) live.Add(new LivePosition((uint)(pc - code), w.Worker, false, where));
                int depth = Math.Clamp((int)*(sbyte*)(l + CodeSwap.LevelDepth), 0, 3);
                for (int d = 0; d < depth; d++) live.Add(new LivePosition(*(uint*)(l + d * 4), w.Worker, true, $"{where} return {d}"));
            }
        }
        return live;
    }

    /// <summary>
    ///     Points everything that holds the old block at the copy: each work's header (<c>+0x00</c>) and block
    ///     (<c>+0x04</c>), its levels' program counters, its trigger table (<c>+0x44</c>) and line table (<c>+0x2e4</c>,
    ///     types 5 and 6); ctrl works' <c>+0x48</c>; the battle managers' block lists (ctrl work <c>+0x1f8</c>). Returns
    ///     the other places in the works that still point into the old block.
    /// </summary>
    private static List<string> Repoint(ScriptBlock loaded, ScriptBlock next, nint old, nint copy, List<Work> works) {
        int length = loaded.Bytes.Length;
        bool Inside(nint p) => p >= old && p < old + length;
        nint Moved(nint p) => copy + CodeSwap.Rebase(loaded, next, (int)(p - old));
        List<string> stale = [];
        foreach (Work w in works) {
            HashSet<int> handled = [0x00, 0x04, 0x44, 0x2e4];
            nint header = *(nint*)w.At;
            *(nint*)w.At = w.Worker >= 0 ? copy + next.Workers[w.Worker].Header : copy + (header - old);
            *(nint*)(w.At + 4) = copy;
            ushort type = w.Worker >= 0 ? loaded.Workers[w.Worker].Type : (ushort)0;
            for (int k = 0; k < CodeSwap.Levels(w.Size, type); k++) {
                int at = CodeSwap.LevelsAt + k * CodeSwap.LevelSize + CodeSwap.LevelPc;
                handled.Add(at);
                nint pc = *(nint*)(w.At + at);
                if (Inside(pc)) *(nint*)(w.At + at) = copy + (pc - old);
            }
            if (w.Size > 0x44 && Inside(*(nint*)(w.At + 0x44))) *(nint*)(w.At + 0x44) = Moved(*(nint*)(w.At + 0x44));
            if (type is 5 or 6 && w.Size > 0x2e4 && Inside(*(nint*)(w.At + 0x2e4))) *(nint*)(w.At + 0x2e4) = Moved(*(nint*)(w.At + 0x2e4));
            for (int at = 0; at + 4 <= w.Size; at += 4) {
                if (!handled.Contains(at) && Inside(*(nint*)(w.At + at))) stale.Add($"worker {w.Worker} +0x{at:x}");
            }
        }
        for (int c = 0; c < CtrlWorks; c++) {
            byte* ctrl = FhUtil.ptr_at<byte>(CtrlWork0 + c * CtrlWorkSize);
            if (*(nint*)(ctrl + 0x48) == old) *(nint*)(ctrl + 0x48) = copy;
        }
        for (int m = 0; m < Managers; m++) {
            byte* manager = FhUtil.ptr_at<byte>(AtelManagers + m * ManagerSize);
            int count = Math.Clamp(*(int*)manager, 0, 8);
            for (int k = 0; k < count; k++) {
                if (*(nint*)(manager + 0x14 + k * 4) == old) *(nint*)(manager + 0x14 + k * 4) = copy;
            }
        }
        return stale;
    }

    public void Dispose() {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
