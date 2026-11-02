// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Ffx.Atel.Bytecode.Vbf;

namespace Ffx.Atel.Cli;

/// <summary>
///     <c>atel export</c>: maintains the private source repo (<c>docs/source-repo.md</c>). Writes
///     <c>atel decompile all</c> onto its <c>generated</c> branch, merges that into <c>main</c> (where the
///     hand edits are) and checks that <c>main</c> still compiles to the game's bytes.
/// </summary>
internal static class ExportCommand {
    public const string Usage = """
          export <repo> [--no-merge] [--no-compile] [--check] [--push]
                                           decompile every script onto the `generated` branch of a git
                                           repo (the whole tree replaced; one commit naming this build
                                           and the game's hashes), merge it into `main` (checked out,
                                           clean; conflicts are listed, not resolved), then compile-all
                                           on main; --push pushes both branches to origin if all passed
        """;

    public const string Generated = "generated";
    public const string Main = "main";

    public static int Run(VbfArchive archive, string gameDir, List<string> args, bool merge, bool compile, bool check, bool push) {
        if (args.Count != 1) throw new ArgumentException("export <repo>");
        string repo = Git(args[0], "rev-parse", "--show-toplevel").Trim();
        string gitDir = Git(repo, "rev-parse", "--absolute-git-dir").Trim();
        if (Git(repo, "status", "--porcelain", "--untracked-files=all").Length > 0) throw new ArgumentException($"{repo} has uncommitted changes");
        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) throw new ArgumentException($"{repo} has a merge in progress");
        string? branch = TryGit(repo, "symbolic-ref", "--short", "HEAD")?.Trim();
        bool mainExists = TryGit(repo, "rev-parse", "--verify", "-q", "refs/heads/" + Main) is not null;
        if (merge && mainExists && branch != Main) throw new ArgumentException($"{repo} is on {branch ?? "a detached HEAD"}; check out {Main} first");

        // The game's hashes, while the decompiler runs: FFX_Data.vbf is about 20 GB.
        System.Threading.Tasks.Task<string> vbfHash = System.Threading.Tasks.Task.Run(() => Sha256(Path.Combine(gameDir, "data", "FFX_Data.vbf")));
        System.Threading.Tasks.Task<string> exeHash = System.Threading.Tasks.Task.Run(() => Sha256(Path.Combine(gameDir, "FFX.exe")));

        string tree = Path.Combine(gitDir, "atel-export");
        string index = Path.Combine(gitDir, "atel-export.index");
        string? commit;
        try {
            if (Directory.Exists(tree)) Directory.Delete(tree, recursive: true);
            File.Delete(index);
            SourceCommands.Decompile(archive, ["all"], tree, share: true);
            File.WriteAllText(Path.Combine(tree, "README.md"), Readme);
            CheckForLocalNames(tree);

            Dictionary<string, string> env = new() { ["GIT_INDEX_FILE"] = index };
            string[] at = ["--git-dir", gitDir, "--work-tree", tree];
            Git(tree, env, [.. at, "add", "-A", "."]);
            // The reference assembly: ignored by the tree's .gitignore (a local decompile writes it), committed here.
            Git(tree, env, [.. at, "add", "-f", "--", ".atel"]);
            string written = Git(tree, env, [.. at, "write-tree"]).Trim();
            int files = Git(tree, env, [.. at, "ls-files"]).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

            string? parent = TryGit(repo, "rev-parse", "--verify", "-q", $"refs/heads/{Generated}^{{commit}}")?.Trim();
            if (parent is not null && Git(repo, "rev-parse", parent + "^{tree}").Trim() == written) {
                Console.WriteLine($"{Generated} is up to date ({parent[..12]})");
                commit = parent;
            }
            else {
                string message = CommitMessage(files, vbfHash.Result, exeHash.Result);
                List<string> commitArgs = ["commit-tree", written, "-F", "-"];
                if (parent is not null) commitArgs.AddRange(["-p", parent]);
                commit = Git(repo, null, commitArgs, message).Trim();
                Git(repo, "update-ref", "-m", "atel export", "refs/heads/" + Generated, commit, parent ?? new string('0', 40));
                Console.WriteLine($"{Generated}: {commit[..12]}, {files} files");
            }
        }
        finally {
            if (Directory.Exists(tree)) Directory.Delete(tree, recursive: true);
            File.Delete(index);
        }

        if (!merge) return 0;
        if (!mainExists) {
            Git(repo, "switch", "-c", Main, Generated);
            Console.WriteLine($"{Main} created at {Generated}");
        }
        else {
            (int code, string output, string error) = RunGit(repo, null, ["merge", "--no-edit", "-m", $"Merge {Generated} ({commit[..12]})", Generated], null);
            if (code != 0) {
                string[] conflicts = (TryGit(repo, "diff", "--name-only", "--diff-filter=U") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (conflicts.Length == 0) throw new IOException($"git merge failed: {error}{output}");
                Console.Error.WriteLine($"merging {Generated} into {Main}: {conflicts.Length} files conflict:");
                foreach (string c in conflicts) Console.Error.WriteLine("  " + c);
                Console.Error.WriteLine($"resolve them in {repo} and commit (or git merge --abort), then: atel compile-all {repo}");
                return 1;
            }
            Console.WriteLine(output.Trim());
        }

        if (!compile) return 0;
        CompileAllCommand.Result result = CompileAllCommand.Compare(archive, repo, "", check);
        if (!result.Ok) return 1;
        if (push) {
            Git(repo, "push", "origin", Generated, Main);
            Console.WriteLine($"pushed {Generated} and {Main}");
        }
        return 0;
    }

    /// <summary>The commit message: this build's commit and the game files it read; no paths, no machine.</summary>
    private static string CommitMessage(int files, string vbf, string exe) {
        string version = typeof(ExportCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        int plus = version.IndexOf('+');
        string source = plus >= 0 ? version[(plus + 1)..] : "unknown";
        string shortSource = source.Length > 12 ? source[..12] : source;
        return string.Create(CultureInfo.InvariantCulture, $"""
            Decompile with ffx-atel-sharp {shortSource}

            atel decompile all, {files} files.

            ffx-atel-sharp: {source} ({version})
            FFX_Data.vbf sha256: {vbf}
            FFX.exe sha256: {exe}

            """);
    }

    /// <summary>
    ///     Refuses to commit a tree that names this machine or the user's home folder (a path in a DLL's
    ///     debug directory, a comment): the source repo carries archive paths only.
    /// </summary>
    private static void CheckForLocalNames(string tree) {
        List<byte[]> needles = [];
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 1) needles.Add(Encoding.UTF8.GetBytes(home));
        if (Environment.MachineName.Length >= 4) needles.Add(Encoding.UTF8.GetBytes(Environment.MachineName));
        List<string> found = [];
        Parallel.ForEach(Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories), file => {
            byte[] bytes = File.ReadAllBytes(file);
            if (needles.Any(n => bytes.AsSpan().IndexOf(n) >= 0)) {
                lock (found) found.Add(Path.GetRelativePath(tree, file));
            }
        });
        if (found.Count > 0) {
            throw new IOException($"the decompiled tree names this machine or the home folder, not committed: {string.Join(", ", found.Order(StringComparer.Ordinal).Take(10))}");
        }
    }

    private static string Sha256(string path) {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Git(string dir, params string[] args) => Git(dir, null, args);

    private static string Git(string dir, Dictionary<string, string>? env, IReadOnlyList<string> args, string? input = null) {
        (int code, string output, string error) = RunGit(dir, env, args, input);
        if (code != 0) throw new IOException($"git {string.Join(' ', args)}: {error.Trim()}");
        return output;
    }

    private static string? TryGit(string dir, params string[] args) {
        (int code, string output, _) = RunGit(dir, null, args, null);
        return code == 0 ? output : null;
    }

    private static (int Code, string Output, string Error) RunGit(string dir, Dictionary<string, string>? env, IReadOnlyList<string> args, string? input) {
        ProcessStartInfo info = new("git") {
            WorkingDirectory = dir,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args) info.ArgumentList.Add(a);
        if (env is not null) {
            foreach ((string key, string value) in env) info.Environment[key] = value;
        }
        using Process process = Process.Start(info) ?? throw new IOException("cannot start git");
        System.Threading.Tasks.Task<string> output = process.StandardOutput.ReadToEndAsync();
        System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync();
        if (input is not null) {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        process.WaitForExit();
        return (process.ExitCode, output.Result, error.Result);
    }

    /// <summary>The source repo's README, written onto <c>generated</c> with every export.</summary>
    private const string Readme = """
        # ffx-atel-src

        **Private. Never make this repository public.** It holds the ATEL scripts of
        Final Fantasy X (events, monster AI, battle scripts) decompiled to C# from a
        player's own copy of the game (the Steam HD Remaster's `FFX_Data.vbf`) with
        [Atel#](https://github.com/yutamago/ffx-atel-sharp) (`yutamago/ffx-atel-sharp`).
        It is the game's code, not ours: it must not be shared.

        ## Branches

        - `generated`: written only by `atel export`. One commit per export: the
          whole tree of `atel decompile all`, replacing the last one, with the
          ffx-atel-sharp commit and the SHA-256 of `FFX_Data.vbf` and `FFX.exe`
          it was made from in the message. Never commit to it by hand.
        - `main`: `generated` merged in, plus hand edits (names, comments,
          reorganised code). Every commit on `main` should still compile to the
          game's bytes, unless an edit is meant to change behaviour.

        ## Layout

        A folder and namespace per script (`Events/Bj/Bjyt0400/`), a file per
        worker, a project per map area (`Events/Bj/Events.Bj.csproj`; blitzball a
        project per script), `Library/` with the base classes of workers shared
        between scripts, `Atel.slnx` (everything) and `Events.slnx`,
        `Battles.slnx`, `Monsters.slnx`. `.atel/Ffx.Atel.Game.dll` (with its XML
        docs) is the reference assembly the source compiles against, from the same
        ffx-atel-sharp build as the source (MIT, our code, not game data; committed so
        the solution opens and builds after a clone).

        ## Working with it

        From an ffx-atel-sharp checkout (`artifacts/bin/Ffx.Atel.Cli/release/atel`),
        with `FFX_GAME_DIR` set to the game folder (the one with `FFX.exe`):

        ```bash
        dotnet build Atel.slnx                 # the source as C#: 0 errors expected
        atel compile-all .                     # every script compiled and compared with the game
        atel compile-all . Events/Bj --check   # one area, also as C#
        atel compile Events/Bj/Bjyt0400 -o bjyt0400.ebp
        ```

        `atel compile-all` lists the scripts that no longer compile to the game's
        bytes; for a hand edit meant only to rename or reorganise, that list must
        be empty.

        After a change to the decompiler, in a clean checkout of `main`:

        ```bash
        atel export . --push   # generated <- decompile all; merge into main; compile-all; push
        ```

        Conflicts between new decompiler output and hand edits on `main` are
        listed and left for you to resolve (`git merge --abort` to back out);
        after resolving, commit and run `atel compile-all .`. See
        ffx-atel-sharp's `docs/source-repo.md`.

        """;
}
