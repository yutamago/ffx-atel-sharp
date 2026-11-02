// SPDX-License-Identifier: MIT
//
// The exported message tables (`atel text export`): read on demand, kept while the file is unchanged, and the
// daemon re-run when a table under a folder in use changes.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JetBrains.Application.Parts;
using JetBrains.Application.Settings;
using JetBrains.Application.Threading;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.Util;

namespace Ffx.Atel.Rider
{
    /// <summary>What a file's <c>Text[n]</c> reads: its scripts, the folder with the tables, the settings.</summary>
    public sealed class TextContext
    {
        public TextContext(IReadOnlyList<ScriptRef> scripts, string root, string language, bool showHints, int maxLength)
        {
            Scripts = scripts;
            Root = root;
            Language = language;
            ShowHints = showHints;
            MaxLength = maxLength;
        }

        public IReadOnlyList<ScriptRef> Scripts { get; }

        /// <summary>The folder with the language folders; null when none was found.</summary>
        public string Root { get; }

        public string Language { get; }
        public bool ShowHints { get; }
        public int MaxLength { get; }
    }

    /// <summary>One script's message <c>n</c>: <see cref="Found" /> false when the script has no table or no entry
    /// <c>n</c> in the language, <see cref="Text" /> null for an entry without text.</summary>
    public sealed class Lookup
    {
        public Lookup(ScriptRef script, bool found, string text)
        {
            Script = script;
            Found = found;
            Text = text;
        }

        public ScriptRef Script { get; }
        public bool Found { get; }
        public string Text { get; }
    }

    [SolutionComponent(Instantiation.DemandAnyThreadSafe)]
    public class TextTables
    {
        private sealed class Entry
        {
            public DateTime Stamp;
            public long Length;
            public TextFile File;
        }

        private readonly Lifetime myLifetime;
        private readonly ISolution mySolution;
        private readonly IShellLocks myLocks;
        private readonly Lazy<IDaemon> myDaemon;
        private readonly ConcurrentDictionary<string, Entry> myFiles = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, FileSystemWatcher> myWatchers = new ConcurrentDictionary<string, FileSystemWatcher>(StringComparer.Ordinal);

        public TextTables(Lifetime lifetime, ISolution solution, IShellLocks locks, Lazy<IDaemon> daemon)
        {
            myDaemon = daemon;
            myLifetime = lifetime;
            mySolution = solution;
            myLocks = locks;
            lifetime.OnTermination(() =>
            {
                foreach (var watcher in myWatchers.Values) watcher.Dispose();
                myWatchers.Clear();
            });
        }

        /// <summary>The context of a source file, from its text, its path and the settings.</summary>
        public TextContext ContextOf(IPsiSourceFile sourceFile, IContextBoundSettingsStore settings)
        {
            var path = sourceFile.GetLocation();
            var text = sourceFile.Document.GetText();
            var key = settings.GetKey<AtelTextSettings>(SettingsOptimization.OptimizeDefault);
            var language = string.IsNullOrWhiteSpace(key.Language) ? "en" : key.Language.Trim();
            var scripts = TextResolver.ScriptsOf(text, path.IsEmpty ? "" : path.FullPath);
            return new TextContext(scripts, RootOf(path, key.TextDirectory), language, key.ShowInlayHints,
                Math.Max(8, key.MaxLength));
        }

        private string RootOf(VirtualFileSystemPath file, string directory)
        {
            string root;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                directory = directory.Trim();
                if (Path.IsPathRooted(directory)) root = directory;
                else
                {
                    var solutionDir = mySolution.SolutionDirectory;
                    root = solutionDir.IsEmpty ? null : Path.Combine(solutionDir.FullPath, directory);
                }
            }
            else root = file.IsEmpty ? null : TextResolver.FindTextRoot(file.FullPath);

            if (root != null) Watch(root);
            return root;
        }

        /// <summary>Message <paramref name="index" /> of each of the context's scripts.</summary>
        public IReadOnlyList<Lookup> Find(TextContext context, int index)
        {
            return context.Scripts.Select(script =>
            {
                var file = context.Root == null ? null : Load(TextResolver.TextFilePath(context.Root, context.Language, script.Name));
                return file != null && file.Has(index)
                    ? new Lookup(script, true, file.Messages[index])
                    : new Lookup(script, false, null);
            }).ToList();
        }

        private TextFile Load(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    myFiles.TryRemove(path, out _);
                    return null;
                }

                if (myFiles.TryGetValue(path, out var cached) && cached.Stamp == info.LastWriteTimeUtc && cached.Length == info.Length)
                    return cached.File;
                var file = TextResolver.ParseTextFile(File.ReadAllText(path));
                myFiles[path] = new Entry { Stamp = info.LastWriteTimeUtc, Length = info.Length, File = file };
                return file;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Re-runs the daemon when a table under <paramref name="root" /> changes (a new export, another
        /// language), as the VS Code extension does with its file watcher.</summary>
        private void Watch(string root)
        {
            if (myLifetime.IsNotAlive || myWatchers.ContainsKey(root) || !Directory.Exists(root)) return;
            FileSystemWatcher watcher;
            try
            {
                watcher = new FileSystemWatcher(root, "*.json") { IncludeSubdirectories = true };
            }
            catch (Exception)
            {
                return;
            }

            if (!myWatchers.TryAdd(root, watcher))
            {
                watcher.Dispose();
                return;
            }

            FileSystemEventHandler changed = (_, __) => Refresh();
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, __) => Refresh();
            watcher.EnableRaisingEvents = true;
        }

        private void Refresh()
        {
            myFiles.Clear();
            myLocks.ExecuteOrQueueEx(myLifetime, "Atel# message text changed", () =>
            {
                if (myLifetime.IsAlive) myDaemon.Value.Invalidate("Atel# message text changed");
            });
        }
    }
}
