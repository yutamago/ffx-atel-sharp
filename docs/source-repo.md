<!-- SPDX-License-Identifier: MIT -->
# The source repo

The decompiled source of every script lives in a private GitHub repo,
[`yutamago/ffx-atel-src`](https://github.com/yutamago/ffx-atel-src) (plan:
"Decisions", Phase 7). It is the game's code, decompiled from the player's
own `FFX_Data.vbf`: **it stays private**, and none of it goes into this repo.
This repo holds only the tool that writes it (`atel export`) and the check
(`atel compile-all`).

## Branches

| Branch | Written by | Holds |
|---|---|---|
| `generated` | `atel export` only | `atel decompile all`, one commit per export, the whole tree replaced (files the decompiler no longer writes disappear) |
| `main` | people, and `atel export`'s merge | `generated` merged in, plus hand edits: names, comments, reorganised code |

A `generated` commit's message names what it was made from, without paths
or machine names:

```
Decompile with ffx-atel-sharp 925f5cd63598

atel decompile all, 19295 files.

ffx-atel-sharp: <commit> (<version>+<commit>)
FFX_Data.vbf sha256: <hex>
FFX.exe sha256: <hex>
```

The commit is the one the `atel` binary was built from (the SDK puts it in
the assembly's informational version), so export from a committed checkout.
Author and committer are the source repo's git identity (`user.name`,
`user.email`; Yutamago and the GitHub noreply address there). Git merges hand
edits with each new decompile as with any other branch.

## Commands

From a build of this repo (`dotnet build -c Release src/Ffx.Atel.Cli`; the
binary is `artifacts/bin/Ffx.Atel.Cli/release/atel`), with `FFX_GAME_DIR`
set:

- **`atel compile-all <dir> [filter] [--check]`** compiles every script
  folder under `<dir>` (the top-most folders holding `.cs` files, outside
  `Library/`, `obj/`, `bin/` and dot folders) with the classes of
  `<dir>/Library/`, and compares each result with the game's file. It lists
  the scripts that differ, the game's scripts without source, and anything
  that does not compile; `--check` also compiles them as C# (Roslyn, against
  `Ffx.Atel.Game`), as `roundtrip --check` does. Exit code 0 only when
  every script of the game compiles to its bytes. A filter (`Events/Bj`)
  limits it to folders whose path contains it, and skips the "without
  source" list.
- **`atel export <repo> [--no-merge] [--no-compile] [--check] [--push]`**,
  with `<repo>` a clean checkout of the source repo on `main`:
  1. hashes `FFX_Data.vbf` and `FFX.exe` (SHA-256) while it decompiles every
     script into `<repo>/.git/atel-export/` and writes the README;
  2. refuses to go on if any file names the user's home folder or the
     machine name;
  3. commits that tree onto `generated` through a separate index (`git add
     -A`, `write-tree`, `commit-tree`, `update-ref`): the working tree and
     `main` are not touched, and nothing is committed when the tree equals
     the last export's;
  4. merges `generated` into `main` (creating `main` on the first export).
     On a conflict it lists the conflicting files, leaves the merge in
     progress and stops;
  5. runs `compile-all` on `main`'s working tree;
  6. with `--push`, pushes both branches to `origin`, only when all of that
     passed.

Both take the machine-wide corpus lock (`GameData.OpenArchiveExclusive`,
`ffx-atel-corpus.lock` in the temp folder) as `roundtrip` and `decompile all`
do: each uses every core and several GB, so they wait for one another.
`dotnet build Atel.slnx` takes no lock; run it on its own too.

## Workflows

**After a decompiler change** (new names, a new layout): commit it here,
build, then in the source repo

```bash
git switch main && git status            # clean
atel export . --push
```

A conflict means the new output changed lines that were edited by hand.
Resolve them (keep the hand edit, adapted to the new output), `git commit`,
`atel compile-all .`, `git push origin main generated`.

**Hand edits on `main`** (rename a worker, a variable, add a comment): edit,
then

```bash
atel compile-all . Events/Bj             # the scripts you touched
atel compile-all .                       # everything, before pushing
dotnet build Events.slnx                 # optional: the IDE's view
```

An edit that only renames or reorganises must leave the list of differing
scripts empty. An edit meant to change behaviour shows up as its script
differing: that is the list to read. Such edits belong in a mod rather than
in `main` (plan, "Mods"); `main` is the vanilla source.

**Never commit to `generated` by hand**: the next export would replace it
anyway, and the merge would then undo it on `main`.

## The reference assembly

`atel decompile -o` copies `Ffx.Atel.Game.dll` and its XML docs to
`.atel/`, which the tree's `Directory.Build.props` references, and its
`.gitignore` ignores `.atel/` (a local decompile is not a repo).
`atel export` commits both anyway (`git add -f`), so a clone of the source
repo opens and builds without a checkout of this repo, against exactly the
API the source was decompiled with. It is our MIT code, not game data, about
1.2 MB per version. To build against a checkout of this repo instead:
`dotnet build Atel.slnx -p:AtelGameProject=<ffx-atel-sharp>/src/Ffx.Atel.Game/Ffx.Atel.Game.csproj`.

`Ffx.Atel.Game.csproj` maps the checkout's path to `/_/` (`PathMap`), so the
PDB path in the DLL's debug directory names no user folder;
`atel export` checks the tree for the home folder and the machine name
before it commits.

## Size

The first export (2026-11-02, ffx-atel-sharp `bb8700c`, `generated` and `main`
at `2fbaf85`): 19,373 files, about 405 MB; the largest is
`Events/Bl/Cn_bltz0000/W36.cs` at 15.7 MB, far below GitHub's 100 MB limit
per file (and its 50 MB warning). Packed, the repo is 15.6 MB. Git LFS is
not needed. On `main`, `atel compile-all .` gave 1,621 / 1,621 scripts
identical (42 s), and `dotnet build Atel.slnx -m:1
-p:UseSharedCompilation=false` 0 errors and 0 warnings (7.7 min; build one
project at a time: the blitzball projects take about 2 GB each).
