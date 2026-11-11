<!-- SPDX-License-Identifier: MIT -->
# Releasing the SDK

What a release takes (plan, Phase 8; Decisions 2026-11-03: **no NuGet release until the user tags one**; the
release decisions of 2026-11-10 below). The first release, `0.1.0-alpha.1` (2026-11-10), put `AtelSharp.Sdk` and
`AtelSharp.Templates` on nuget.org; the next one adds `AtelSharp.Cli`.

The packages:

| Package | What | Built from |
|---|---|---|
| `AtelSharp.Sdk` | the MSBuild project SDK: `Sdk.props`/`Sdk.targets`, the reference assembly `Ffx.Atel.Game` with its docs, the analyzers and code fixes, the `atel` tool | `src/Ffx.Atel.Sdk` ([sdk.md](sdk.md)) |
| `AtelSharp.Templates` | `dotnet new atel-mod` | `src/Ffx.Atel.Templates` ([sdk.md](sdk.md#dotnet-new-atel-mod)) |
| `AtelSharp.Cli` | the `atel` CLI as a .NET tool: `dotnet tool install -g AtelSharp.Cli`, command `atel` (from `0.1.0-alpha.2`) | `src/Ffx.Atel.Cli` ([sdk.md](sdk.md#the-atel-tool)) |

The project folders, project files, assemblies and C# namespaces keep their `Ffx.Atel` names; only the package ids
are `AtelSharp.*`. A project SDK resolves by package id, so a mod is `<Project Sdk="AtelSharp.Sdk">` and
`global.json` names `AtelSharp.Sdk` under `msbuild-sdks`.

The editor extensions (`editors/vscode`, `editors/rider`) are not NuGet packages; they have versions of their own
(`0.1.0`), and a release attaches their packages to its GitHub release. The loader is not part of a release.

## Decisions (user, 2026-11-10)

1. **nuget.org.** The workflow keeps its switch: the repository variable `ATEL_NUGET_TARGET` (`nuget.org`, or
   `github` for GitHub Packages). It pushes with nuget.org's Trusted Publishing: no stored API key.
2. **Package ids `AtelSharp.Sdk` and `AtelSharp.Templates`**, under the prefix `AtelSharp`, reserved on nuget.org for
   the owner's account. The template's short name stays `atel-mod`.
3. **The first version is `0.1.0-alpha.1`** (CHANGELOG.md has its section).
4. **The template ships as its own package**, `AtelSharp.Templates`, with every release (`ATEL_PUBLISH_TEMPLATES`
   is now an opt-out: `false` leaves it out).
5. **A mod from the template is MIT by default**: `LICENSE` (the year, and `--authors` as the copyright holder; "the
   authors of this mod" when `--authors` is left at its default), `<PackageLicenseExpression>MIT` in the project and
   `// SPDX-License-Identifier: MIT` in its `.cs` file. `--license none` writes none of the three
   ([sdk.md](sdk.md#dotnet-new-atel-mod)).
6. **Fahrenheit's names**: the reference assembly's enums and save fields carry names from Fahrenheit's ID tables
   and `SaveData`, used with the Fahrenheit project's permission, which covers the public package.
7. **The tag is `v<version>`.** With `v*`, a tag also names the version of the whole repository; if the editors or
   the loader get releases of their own later, they need their own prefixes.
8. **A GitHub release** for each tag, with every `.nupkg`, the VS Code `.vsix`, the Rider plugin `.zip` and the
   version's CHANGELOG section as notes; a version with a suffix (`-alpha.1`) is a pre-release.

Added later (website plan, [website.md](website.md#the-cli-as-a-net-tool)): **`AtelSharp.Cli`**, the `atel` CLI as a
.NET tool package, released with the others from `0.1.0-alpha.2`, same version, MIT. The SDK package keeps its copy
under `tools/atel/`, which its targets run. The Trusted Publishing policy covers it as it covers any package the
workflow pushes for the account.

## Versions

**Today, every commit has its own version**: `0.1.0-alpha.0.<commits>.g<commit>`, from git
(`Directory.Build.targets`, [sdk.md](sdk.md#versions)). NuGet caches a package by version and never reads that
version again, so a changed SDK needs a new one; the CLI pins the SDK of its own version in what it writes
(`global.json`), and the template pins the SDK of the template's version.

**A release has the version of its tag**: `v0.1.0-alpha.1` builds `0.1.0-alpha.1` (`-p:Version=`, which skips the
git version). By SemVer's order:

```
0.1.0-alpha.0.245.g8e0a942292   a commit before the release
0.1.0-alpha.1                   the first release (1 > 0 in the first differing identifier)
0.1.0-alpha.1.260.g1234567890   a commit after it, once AtelVersionBase is 0.1.0-alpha.1
0.1.0-alpha.2                   the next release
```

So:

1. The first release is `0.1.0-alpha.1` or anything above `0.1.0-alpha.0.*` (`0.1.0-alpha` is *below* them: never
   use it). A stable `0.1.0` sorts above every prerelease of 0.1.0.
2. After a release, set `AtelVersionBase` in `Directory.Build.props` to the released version, so that the commits
   after it sort above it and below the next release.
3. A version is never released twice: nuget.org keeps every version forever (unlisting hides it, nothing
   deletes it), and caches keep it too.

## The release workflow

`.github/workflows/release-sdk.yml` runs **only on a pushed tag `v*`**: no branch, pull request or manual run starts
it, and no other workflow publishes (`build.yml` only packs, and installs the packed `atel` tool into a folder of its own to run it once). Its job `packages`:

1. takes the version from the tag (`v0.1.0-alpha.1` → `0.1.0-alpha.1`) and stops unless it is a SemVer 2 version;
2. stops unless the tagged commit is on `main`;
3. stops unless `CHANGELOG.md` has a section `## [<version>]`;
4. builds and tests the solution (the corpus tests skip: CI has no game);
5. packs the three packages with `-p:Version=<version>`, checks that none names the runner's checkout path and that
   the template pins the SDK of the same version;
6. installs `AtelSharp.Cli` from the packed file into a tool path of its own and runs `atel --help`;
7. keeps them as the run's artifact `packages-<version>`.

The job `editors` builds and tests the VS Code extension and the Rider plugin (`.github/workflows/editors.yml`, the
same jobs `build.yml` runs on every push; the Rider build downloads the Rider SDK, a few GB) and keeps the `.vsix`
and the plugin `.zip`.

Once both pass, the job `publish` pushes the packages (`--skip-duplicate`, so a re-run pushes nothing twice),
**only** where the repository variable `ATEL_NUGET_TARGET` says:

- `nuget.org`: through Trusted Publishing. The job (with `id-token: write`) runs `NuGet/login` for the account in the
  repository variable `NUGET_USER`, which swaps GitHub's OIDC token for an API key valid for this run only;
- `github`: to GitHub Packages of the repository's owner (`https://nuget.pkg.github.com/yutamago/index.json`), with
  the job's `GITHUB_TOKEN`;
- unset: it stops with an error before pushing anything, and no GitHub release is made.

`AtelSharp.Sdk` and `AtelSharp.Cli` always go; `AtelSharp.Templates` goes along unless the variable
`ATEL_PUBLISH_TEMPLATES` is `false`.

After the push, the job `release` (the only job with `contents: write`) creates the GitHub release of the tag with
`gh release create --verify-tag`: title `Atel# <version>`, the version's section of `CHANGELOG.md` as notes, and as
assets `AtelSharp.Sdk.<version>.nupkg`, `AtelSharp.Templates.<version>.nupkg`, `AtelSharp.Cli.<version>.nupkg`,
`atel-text-vscode-<version>.vsix` and
`atel-text-rider-<version>.zip` (the editors' own versions). A version with a prerelease suffix is marked a
pre-release.

## Before the first release (the user, once)

1. A nuget.org account (sign in with a Microsoft account at https://www.nuget.org).
2. **Reserve the prefix** `AtelSharp`: nuget.org's ID prefix reservation is requested by e-mail to
   `account@nuget.org`, naming the account and the prefix `AtelSharp.*`
   (https://learn.microsoft.com/nuget/nuget-org/id-prefix-reservation). Once granted, only that account can push
   `AtelSharp.*`, and the packages show the verified mark. The first push does not wait for it: the ids are free
   until someone pushes them.
3. **A Trusted Publishing policy** (nuget.org → Trusted Publishing; done 2026-11-10): repository owner `yutamago`,
   repository `ffx-atel-sharp`, workflow file `release-sdk.yml`, no environment. No API key or secret is needed.
4. **The variable** `NUGET_USER` = the nuget.org account name (`yutamago`), on the repository's Settings → Secrets
   and variables → Actions → Variables.
5. **The variable** `ATEL_NUGET_TARGET=nuget.org` (same page, Variables tab): the maintainer's tooling sets it with the
   release setup; if it is missing at tag time, the run stops before pushing (add it, then re-run the failed jobs).

## Steps of a release

1. On `main`, move the `## [Unreleased]` entries of `CHANGELOG.md` into `## [<version>] - <date>` and merge that.
   For `0.1.0-alpha.1` this is done (dated 2026-11-10; change the date if the tag comes later). Its links point at
   the tag (`blob/v0.1.0-alpha.1/…`), since the GitHub release shows the section as it is.
2. Check `ATEL_NUGET_TARGET` and `NUGET_USER`, and that the Trusted Publishing policy still exists ("Before the first release").
3. `git tag v<version> <commit on main> && git push origin v<version>`; for the first:
   `git tag v0.1.0-alpha.1 && git push origin v0.1.0-alpha.1` on an up-to-date `main`.
4. Watch the run: the packages, then nuget.org, then the GitHub release. nuget.org validates and indexes a pushed
   package for a few minutes before `dotnet new install` and restore find it.
5. After it: `AtelVersionBase` in `Directory.Build.props` to `<version>` (Versions, step 2), and the docs that say the
   package is not published ([sdk.md](sdk.md) "What you need", the template's README "The SDK package") to the
   source modders use.

## Package metadata

Set in `Directory.Build.props` and each package's project:

| | `AtelSharp.Sdk` | `AtelSharp.Templates` | `AtelSharp.Cli` |
|---|---|---|---|
| Id | `AtelSharp.Sdk` | `AtelSharp.Templates` (template identity `AtelSharp.Templates.Mod`, short name `atel-mod`) | `AtelSharp.Cli` (command `atel`) |
| Package type | `MSBuildSdk` | `Template` | `DotnetTool` (`PackAsTool`, `ToolCommandName`) |
| Description, tags | the project's `Description`, `PackageTags` | the same | the same |
| Authors, copyright | `Yutamago`, `Copyright (c) 2026 Yutamago` | the same | the same |
| License | `MIT` (`PackageLicenseExpression`) | the same | the same |
| Project and repository URL | `https://github.com/yutamago/ffx-atel-sharp` | the same | the same |
| Readme | `src/Ffx.Atel.Sdk/README.md` | `src/Ffx.Atel.Templates/README.md` | `src/Ffx.Atel.Cli/README.md` |
| Release notes | `CHANGELOG.md` | the same | the same |
| Icon | none yet | none yet | none yet |
