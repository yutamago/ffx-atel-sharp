# Plan: the Atel# website

A site on GitHub Pages that introduces Atel# to developers and teaches them to
write mods with it. It renders the Markdown in `docs/`, adds a landing page,
task guides and an API reference generated from the game library, and deploys
from `main`.

Status: planned 2026-11-10. Decisions marked **(open)** wait on the user.

## Who it is for

1. **C# developers who don't know FFX modding.** They need to know what ATEL is,
   what they can change with it, and how to get a first mod running in under an
   hour.
2. **FFX modders who already use Fahrenheit.** They know the game and the
   loader. They need to know how the C# maps to what they know, and where an
   Atel# mod stops and a Fahrenheit code mod starts.
3. **Contributors.** They want the encoding, the formats and the research
   behind the compiler. This is the smallest group, and the repo already serves
   it, so the site links to that material and keeps it out of the way.

The site should answer three questions in this order: what is this, can I make
my first mod, and where do I look things up.

## Where it lives

- URL: `https://yutamago.github.io/ffx-atel-sharp/` **(open: a custom domain
  instead?)**. The repo's "Website" field and the README link to it.
- Pages source: GitHub Actions (`actions/upload-pages-artifact` and
  `actions/deploy-pages`), not a `gh-pages` branch, so no generated files land
  in git.
- The text stays in `docs/`. The site project in `site/` holds only the
  configuration, the landing page, the navigation and the theme. A page
  that exists on GitHub and on the site has one source file, so the two never
  drift apart.

## Generator

**(open)** Two candidates:

| | DocFX (recommended) | Astro Starlight |
|---|---|---|
| Toolchain | a .NET tool (`dotnet tool install docfx`), same as the repo | Node and npm, new to this repo |
| API reference | built in, from `Ffx.Atel.Game`'s XML docs (7,115 `<summary>` comments) | needs a second step (DocFX metadata or xmldoc2md) |
| Code from `examples/` | `[!code-csharp[](…)]` with regions | an import component or a remark plugin |
| Landing page | plainer; a custom template or one hand-written HTML page | flexible; components, cards, hero |
| Search, dark mode | built in (modern template) | built in |

The API reference is the main reason to pick DocFX: `Btl`, `Actors`, `Camera`,
`Text`, `Save`, `Wait` and the enums are what a modder looks up most, and their
docs already exist. Starlight makes the nicer landing page but adds a Node
toolchain and a separate API step. If the landing page in DocFX turns out too
plain, it can be one hand-written HTML file in `site/` without changing the
generator.

## Site map

### Home

One screen, then links.

- One sentence: Atel# decompiles FFX's ATEL scripts to C#, and compiles your
  C# back into the game through Fahrenheit.
- A short mod next to what it does in game, taken from `examples/ToughLarva`
  (our code, MIT). No vanilla decompiled source.
- Three steps with links: decompile, write a `partial` class, `dotnet build`
  and drop the package into Fahrenheit.
- The install lines (`dotnet new install AtelSharp.Templates`), the latest
  release and the NuGet badges.
- The numbers that matter: 1,621 of 1,621 scripts round-trip byte for byte;
  mods link onto the player's own files, so no game file is ever shipped.

### Get started

- **Requirements.** The Steam HD Remaster, Fahrenheit, the .NET SDK version,
  and where each comes from.
- **Install.** The templates package, the SDK, the editor extensions (the
  `.vsix` and the Rider zip from the GitHub release), and the `atel` CLI.
- **Quickstart.** `dotnet new atel-mod`, build, install, see it in game. Five
  minutes, no explanation beyond what each command does.
- **Your first mod.** `tutorial.md` (the tougher Larva), split into one page
  per step with a "next" link.

The CLI is not packaged yet: today a modder builds it from source to run
`atel decompile`. **(open)** Publish it as a .NET tool (`AtelSharp.Cli`,
`dotnet tool install -g AtelSharp.Cli`) with the next release, so "Install" is
one command. Without it the Quickstart has to start with `git clone` and
`dotnet build`.

### Guides

Task pages, each built around one project in `examples/` that CI compiles, so
no snippet on the site can go stale without a red build.

| Guide | Example | Source in `docs/` today |
|---|---|---|
| Change a monster's stats | `LarvaHp`, `ToughLarva` | tutorial.md |
| Add code into an existing function with `[Inject]` | `LarvaInject` | language.md, plan.md Phase 9 |
| Keep data in the save with `[Saved]` | `LarvaCount`, `LarvaCountReader` | research/mod-saves.md |
| Change data per field | (new example) | language.md |
| Edit and add message text | (new example) | editor.md |
| Print debug text in game (F6 overlay) | (new example) | formats/debug.md |
| Switch a dead `if (false)` block on | (new example) | sdk.md, research/dead-code.md |
| Combine with a Fahrenheit code mod | | sdk.md |
| Set up VS Code or Rider | | editor.md |
| Find the script you want to change | | handlers.md, corpus.md |

The guides that have no example yet get one in `examples/` first; the page
comes second.

### Concepts

What a modder needs in their head before the reference makes sense. Most of it
is in `language.md` and `research/` today, written for the compiler; the site
pages say it from the modder's side.

- The ATEL VM in brief: scripts, workers, entries, the 30 Hz frame.
- Requests between workers, and waiting.
- Variables: script fields, save fields, registers, locals.
- How a mod gets into the game: the package, the linker, the loader, and what
  happens when two mods touch the same entry (the conflict report).
- What Atel# can't do: no .NET at runtime, the analyzer rules that say why.

### Reference

- **Language.** `language.md`, split at its `##` headings into pages.
- **Game library API.** Generated from `Ffx.Atel.Game` (XML docs). One page
  per class, with the enums and the natives they wrap.
- **Analyzers.** One page per rule (`ATL0001` … `ATL4004`). Each analyzer gets
  a `HelpLinkUri` pointing at its page, so the link in the IDE's error list
  opens the site. None of the analyzers set it today.
- **SDK properties.** From `sdk.md`.
- **CLI.** One page per command, generated from `atel <command> --help` at
  build time so it matches the shipped tool.
- **Mod package and `mod.toml`.** From `formats/mod-package.md`.

### How it works

For contributors: `encoding.md`, `formats/script-block.md`,
`formats/debug.md`, `corpus.md`, the `research/` pages, `plan.md`. Rendered as
they are, under one menu entry. **(open)** On the site at all, or link to the
repo instead? Rendering them costs nothing but makes search return compiler
internals for modder questions; a separate search scope or a lower ranking
fixes that.

### Project

The changelog, the releases, contributing, the MIT licence, and the line about
Fahrenheit names ("with the Fahrenheit project's permission", no person named).

## Content rules

- No game files, no extracted data, no decompiled vanilla scripts. Short
  excerpts with `…` as in the tutorial are the limit. Examples come from
  `examples/`.
- **(open)** Screenshots or short clips from the game, to show a mod working:
  the user's own captures only, or none at all?
- Story names keep their number first.
- Don't name the Fahrenheit person; say "with the Fahrenheit project's
  permission".
- Write for a reader who knows C# and not ATEL: say what a native does before
  its number, and give hex addresses only in "How it works".
- Links between `docs/` pages stay relative Markdown links, so they work on
  GitHub and on the site.

## Build and deploy

A new workflow, `.github/workflows/pages.yml`:

1. **Build** on every PR and on `main`, filtered to `docs/**`, `site/**`,
   `examples/**`, `src/Ffx.Atel.Game/**`, `src/Ffx.Atel.Analyzers/**` and
   `src/Ffx.Atel.Cli/**`.
   - `dotnet build src/Ffx.Atel.Game` for the XML docs.
   - Run the CLI's `--help` for each command into `site/cli/`.
   - `docfx metadata` then `docfx build --warningsAsErrors`, so a broken link
     or a missing snippet region fails the PR.
   - A link check of the output (lychee) for external links, weekly and not
     on every PR, so a slow outside site can't block a merge.
2. **Deploy** only on `main`: `upload-pages-artifact`, then `deploy-pages`
   with `pages: write` and `id-token: write`, environment `github-pages`.

Pages has to be switched to "GitHub Actions" once in the repo settings (or
with `gh api -X POST repos/yutamago/ffx-atel-sharp/pages -f build_type=workflow`).

Versions: one site, built from `main`, with the latest release number in the
header. Versioned docs (one per release) wait until there is a second
release whose API differs.

## Steps

1. **Skeleton.** `site/` with the DocFX config, the navigation, `docs/` rendered
   as it is, a plain landing page, `pages.yml`, Pages switched on, README and
   repo homepage link. Done when the site is live and every current doc page
   renders with working links.
2. **Reference.** The API pages from `Ffx.Atel.Game`, one page per analyzer
   rule with `HelpLinkUri` set, the CLI pages, `language.md` split. Done when
   clicking an `ATL` code in Rider and VS Code opens its page.
3. **Get started.** Requirements, install, the Quickstart, the tutorial split
   into steps. Needs the CLI decision; if the CLI becomes a .NET tool, that
   lands first, with a release.
4. **Guides.** The missing examples, then one page per guide.
5. **Concepts.** The modder-side pages.
6. **Landing page and polish.** The real home page, a social preview image,
   a 404 page, search scopes, a check at phone width and in light and dark.

Each step is one PR with CI green and `origin/main` merged in. Steps 2, 4 and
5 can run in parallel once step 1 is merged.

## Open decisions

1. Generator: DocFX (recommended) or Astro Starlight.
2. URL: `yutamago.github.io/ffx-atel-sharp` or a custom domain.
3. Publish the CLI as a .NET tool (`AtelSharp.Cli`) so install is one command.
4. "How it works" pages: on the site, or links to the repo.
5. Game screenshots or clips: the user's own captures, or none.
