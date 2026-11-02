// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using Fahrenheit;

using Ffx.Atel.Bytecode;
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
///         in <see cref="Update"/>, which the module calls before each frame's update on the game thread.
///     </para>
///     <para>
///         A package's file is rewritten in the loader's <c>efl/x</c> only when Fahrenheit's file loader serves
///         it, i.e. when the file was there at startup: its index is built once. A package for another file
///         takes effect from the next start, when the loader links it with the rest.
///     </para>
/// </remarks>
internal sealed unsafe class RuntimeInjector(string loaderDir, string gameDir, string eflDir, IReadOnlyList<AtelMod> linked,
                                             IReadOnlySet<string> served, IReadOnlySet<string> taken, FhLogger log) : IDisposable {
    // Offsets from the image base (FFX.exe at 0x00400000).
    /// <summary>Ctrl work 0, the event's (<c>0x01325ba0</c>): <c>+0</c> bit 0 in use, <c>+0x34</c>/<c>+0x38</c> its message tables.</summary>
    private const nint CtrlWork0 = 0x00f25ba0;
    /// <summary><c>g_EventName</c> (<c>0x023cbc60</c>): the event's folder and name, <c>ka/kami0000</c>.</summary>
    private const nint EventName = 0x01fcbc60;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetLangFn();

    /// <summary><c>TOGetFFXLang</c> (<c>0x006410e0</c>): the language whose <c>new_&lt;lang&gt;pc</c> files the game reads.</summary>
    private static FhCallHandle<GetLangFn> TOGetFFXLang => new(new FhCallLocation("FFX.exe", 0x002410e0));

    private readonly object _lock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private Dictionary<string, string> _seen = new(StringComparer.Ordinal);
    private volatile object? _lang;       // TOGetFFXLang, boxed, once the game is running
    private volatile bool _full;

    // Messages per event script, set by the background thread; Update applies them.
    private volatile Dictionary<string, List<(int Index, byte[] Text)>> _messages = new(StringComparer.OrdinalIgnoreCase);
    private int _version;
    private int _appliedVersion = -1;
    private nint _installed;       // our table in ctrl work 0 +0x34, or 0
    private nint _original;        // the game's table we replaced
    private readonly List<nint> _ours = [];

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
            Dictionary<string, List<(int, byte[])>> messages = new(StringComparer.OrdinalIgnoreCase);
            if (_lang is not int ffxLang) return;     // Update asks again once the game runs
            foreach (ScriptPackage p in injected.Packages.Values.Where(p => p.Messages is { Count: > 0 })) {
                if (GameData.Identify(p.File) is not (ScriptCategory.Event, _)) {
                    log.Warning($"Atel loader: inject: {p.Script}: messages are set only in an event's table; left out");
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
                    LinkResult? relinked = Injection.Relink(linked, injected, path, readVanilla).FirstOrDefault()?.Result;
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
                if (list.Count > 0) messages[p.Script] = list;
            }
            _messages = messages;
            Interlocked.Increment(ref _version);
            if (messages.Count > 0) {
                log.Info($"Atel loader: inject: messages for {string.Join(", ", messages.Keys)} ({Injection.Language(ffxLang) ?? GameLanguage.English}); set when the event runs");
            }
        }
        finally {
            archive?.Dispose();
        }
    }

    private void Relink(AtelMod injected, string path, Func<string, byte[]?> readVanilla) {
        if (!served.Contains(path)) {
            log.Warning($"Atel loader: inject: {path} was not served at startup, so the game cannot be given a new copy now; it is linked from the next start");
            return;
        }
        string target = Path.Combine(eflDir, path.Replace('/', Path.DirectorySeparatorChar));
        List<LinkedFile> files = Injection.Relink(linked, injected, path, readVanilla);
        List<LinkMessage> report = [];
        bool ok;
        if (files.Count == 0) {
            // No package left for the file: the vanilla one.
            byte[]? vanilla = readVanilla(path);
            ok = vanilla is not null;
            if (ok) File.WriteAllBytes(target, vanilla!);
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
    }

    /// <summary>Sets the running event's messages when they changed or the event loaded again. Game thread.</summary>
    public void Update() {
        try {
            byte* ctrl = FhUtil.ptr_at<byte>(CtrlWork0);
            if ((ctrl[0] & 1) == 0) return;
            if (_lang is null) {
                // An event runs, so its messages were read in the game's language (TOGetFFXLang answers 0
                // before its manager exists). Encode the packages' messages now.
                _lang = GetLanguage();
                _full = true;
                _debounce?.Change(0, Timeout.Infinite);
                return;
            }
            nint table = *(nint*)(ctrl + 0x34);
            if (table == 0) return;
            int version = Volatile.Read(ref _version);
            if (table == _installed && version == _appliedVersion) return;
            string? eventName = Marshal.PtrToStringAnsi((nint)FhUtil.ptr_at<byte>(EventName));
            if (eventName is null) return;
            Dictionary<string, List<(int, byte[])>> all = _messages;
            KeyValuePair<string, List<(int, byte[])>> mine = all.FirstOrDefault(kv => Injection.IsEvent(eventName, kv.Key));
            if (table != _installed) {
                // Another event, or the same loaded again: the game's own table.
                _original = table;
                _installed = 0;
                FreeOurs();
            }
            _appliedVersion = version;
            nint baseTable = _original;
            if (mine.Value is null) {
                if (_installed != 0) Install(ctrl, baseTable);    // the package went away: the game's table again
                return;
            }
            int? length = Injection.TableLength(o => *(ushort*)(baseTable + o), o => *(byte*)(baseTable + o));
            if (length is null) {
                log.Error($"Atel loader: inject: {eventName}: the event's message table does not look like one; not changed");
                return;
            }
            byte[] bytes = Injection.SetMessages(new ReadOnlySpan<byte>((void*)baseTable, length.Value), mine.Value);
            nint ours = (nint)NativeMemory.Alloc((nuint)bytes.Length);
            Marshal.Copy(bytes, 0, ours, bytes.Length);
            _ours.Add(ours);
            Install(ctrl, ours);
            log.Info($"Atel loader: inject: {eventName}: {mine.Value.Count} message(s) set ({length} -> {bytes.Length} bytes)");
        }
        catch (Exception ex) {
            _appliedVersion = Volatile.Read(ref _version);
            log.Error($"Atel loader: inject: messages: {ex}");
        }
    }

    private static int GetLanguage() => TOGetFFXLang.fnptr!();

    /// <summary>Points ctrl work 0's tables at <paramref name="table"/>: <c>+0x34</c>, and <c>+0x38</c> where it was the same table.</summary>
    private void Install(byte* ctrl, nint table) {
        nint current = *(nint*)(ctrl + 0x34);
        if (*(nint*)(ctrl + 0x38) == current) *(nint*)(ctrl + 0x38) = table;
        *(nint*)(ctrl + 0x34) = table;
        _installed = table == _original ? 0 : table;
    }

    /// <summary>
    ///     Frees our tables of an earlier event (the game frees only its own, through <c>0x01327128</c>). The
    ///     latest stays allocated, as a message window may still show its text.
    /// </summary>
    private void FreeOurs() {
        for (int i = 0; i < _ours.Count - 1; i++) NativeMemory.Free((void*)_ours[i]);
        if (_ours.Count > 1) _ours.RemoveRange(0, _ours.Count - 1);
    }

    public void Dispose() {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
