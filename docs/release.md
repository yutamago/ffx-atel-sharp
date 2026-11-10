<!-- SPDX-License-Identifier: MIT -->
# Releasing the SDK

What a release of the NuGet packages takes, written down before the first one (plan, Phase 8; Decisions
2026-11-03: **no NuGet release until the user tags one**). Nothing is published yet, and nothing can be until the
user pushes a tag and sets where the packages go (below).

The packages:

| Package | What | Built from |
|---|---|---|
| `Ffx.Atel.Sdk` | the MSBuild project SDK: `Sdk.props`/`Sdk.targets`, the reference assembly `Ffx.Atel.Game` with its docs, the analyzers and code fixes, the `atel` tool | `src/Ffx.Atel.Sdk` ([sdk.md](sdk.md)) |
| `Ffx.Atel.Templates` | `dotnet new atel-mod` | `src/Ffx.Atel.Templates` ([sdk.md](sdk.md#dotnet-new-atel-mod)) |

The editor extensions (`editors/vscode`, `editors/rider`) and the loader are not NuGet packages and are not part of
this release; they have versions of their own (`0.1.0`).

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
it, and no other workflow publishes (`build.yml` only packs, as before). It:

1. takes the version from the tag (`v0.1.0-alpha.1` → `0.1.0-alpha.1`) and stops unless it is a SemVer 2 version;
2. stops unless the tagged commit is on `main`;
3. stops unless `CHANGELOG.md` has a section `## [<version>]`;
4. builds and tests the solution (the corpus tests skip: CI has no game);
5. packs both packages with `-p:Version=<version>`, checks that neither names the runner's checkout path and that the
   template pins the SDK of the same version, and keeps them as the run's artifact;
6. pushes, **only** when the repository variable `ATEL_NUGET_TARGET` says where:
   - `nuget.org`: with the repository secret `NUGET_API_KEY` (a nuget.org API key scoped to pushing the package ids
     below);
   - `github`: to GitHub Packages of the repository's owner (`https://nuget.pkg.github.com/yutamago/index.json`),
     with the job's `GITHUB_TOKEN`;
   - unset (as now): it stops with an error before pushing anything.

   `Ffx.Atel.Templates` is pushed only with the variable `ATEL_PUBLISH_TEMPLATES` set to `true`.

The workflow creates no GitHub release; the packages are the run's artifact.

## Steps of a release

1. On `main`, move the `## [Unreleased]` entries of `CHANGELOG.md` into `## [<version>] - <date>` and merge that.
2. Set `ATEL_NUGET_TARGET` (and `NUGET_API_KEY`, or `ATEL_PUBLISH_TEMPLATES`) in the repository's settings,
   Secrets and variables → Actions, if not yet set.
3. `git tag v<version> <commit on main> && git push origin v<version>`.
4. Watch the run; the packages are its artifact either way.
5. After it: `AtelVersionBase` in `Directory.Build.props` to `<version>` (Versions, step 2), and the docs that say the
   package is not published ([sdk.md](sdk.md) "What you need", the template's README "The SDK package") to the
   source modders use.

## Package metadata

Set in `Directory.Build.props` and each package's project:

| | `Ffx.Atel.Sdk` | `Ffx.Atel.Templates` |
|---|---|---|
| Id | `Ffx.Atel.Sdk` (free on nuget.org, 2026-11-02) | `Ffx.Atel.Templates` (not checked) |
| Package type | `MSBuildSdk` | `Template` |
| Description, tags | the project's `Description`, `PackageTags` | the same |
| Authors, copyright | `Yutamago`, `Copyright (c) 2026 Yutamago` | the same |
| License | `MIT` (`PackageLicenseExpression`) | the same |
| Project and repository URL | `https://github.com/yutamago/ffx-atel-sharp` | the same |
| Readme | `src/Ffx.Atel.Sdk/README.md` | `src/Ffx.Atel.Templates/README.md` |
| Release notes | `CHANGELOG.md` | the same |
| Icon | none yet | none yet |

## Decisions for the user

Before the first tag:

1. **Where the packages go**: nuget.org (public, permanent, found by `dotnet new install` and the SDK resolver with no
   setup) or GitHub Packages (needs a GitHub token in every modder's `nuget.config`, even for a public repository).
   Set `ATEL_NUGET_TARGET` accordingly, and for nuget.org the account that owns the ids and its API key.
2. **The package ids**: `Ffx.Atel.Sdk` and `Ffx.Atel.Templates`, or others (a prefix reserved on nuget.org,
   `Yutamago.*`). The id is in every mod project and `global.json`; changing it later breaks them.
3. **The first version**: `0.1.0-alpha.1` (above every per-commit build), or another prerelease, or `0.1.0`.
4. **Whether the template ships**: as its own package `Ffx.Atel.Templates` (as built now; `ATEL_PUBLISH_TEMPLATES`),
   inside the SDK package (one id, but a package with two package types is unusual and `dotnet new install` would
   download the whole SDK), or not at all yet.
5. **Licence headers and terms**: the repository's files carry `SPDX-License-Identifier: MIT`; the template writes
   modders' files **without** a header (they are the modder's code, under the modder's licence). Whether that is
   right, and whether the permission to use Fahrenheit's ID-table names (in the reference assembly's enums) covers a
   public package ([sdk.md](sdk.md#publishing)).
6. **The tag**: `v*`, as the brief of 2026-11-09 asked, replaces the `sdk-v*` of the 2026-11-03 decision. With
   `v*`, a tag also names the version of the whole repository; if the editors or the loader get releases of their
   own later, they need their own prefixes.
7. **A GitHub release** for the tag, with the packages, the VS Code `.vsix` and the Rider plugin `.zip`: not made
   now.
