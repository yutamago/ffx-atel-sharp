# Plan: the Atel# website

A site on GitHub Pages that introduces Atel# to developers and teaches them to
write mods with it.

Status: planned 2026-11-10; the user's decisions of the same day are in
[Decisions](#decisions).

## Who it is for

1. **C# developers who don't know FFX modding.** They need to know what ATEL is,
   what they can change with it, and how to get a first mod running in under an
   hour.
2. **FFX modders who already use Fahrenheit.** They know the game and the
   loader. They need to know how the C# maps to what they know, and where an
   Atel# mod stops and a Fahrenheit code mod starts.
3. **Contributors.** They want the encoding, the formats and the research
   behind the compiler. The repo serves them already; the site gives them a
   short overview and links into `docs/`.

The site should answer three questions in this order: what is this, can I make
my first mod, and where do I look things up.

## The site is written for people

The Markdown in `docs/` is the working reference of the project: dense, exact,
full of addresses and measurements, and written as much for agents as for
people. The site does not render it. Each site page is written new, for a
reader who knows C# and not ATEL:

- One idea per paragraph, examples before rules, and the reason a thing
  works the way it does when the reader would otherwise trip over it.
- Native numbers, offsets and corpus counts stay out of the main text. Where a
  reader may want them, a "Details" link at the end of the page points at the
  matching section of `docs/` on GitHub.
- Terms are introduced once, in [Concepts](#concepts), and then used the same
  way everywhere (worker, entry, request, script field).
- Every code block comes from a project that CI compiles (see
  [Code on the site](#code-on-the-site)).

`docs/` stays the source of truth for facts. When a fact changes there, the
site page that says it in plain words changes in the same PR. A check in CI
can't catch a sentence that has gone stale, so each site page lists the
`docs/` sections it is based on in its front matter (`sources:`), and the PR
template asks whether a changed `docs/` section has a site page.

## Where it lives

- URL: `https://yutamago.github.io/ffx-atel-sharp/`, for now. A custom domain
  can come later without changing the site (Astro's `site` and `base`
  settings).
- Pages source: GitHub Actions (`actions/upload-pages-artifact` and
  `actions/deploy-pages`), not a `gh-pages` branch, so no generated files land
  in git.
- The site is an [Astro Starlight](https://starlight.astro.build/) project in
  `site/`: the pages in `site/src/content/docs/`, the landing page, the images
  and clips in `site/src/assets/`, the theme. Node and npm are used only in
  `site/`, pinned with `.nvmrc` and `package-lock.json`.

## Site map

### Home

One screen, then links.

- One sentence: Atel# decompiles FFX's event and battle scripts to C#, and
  compiles your C# back into the game through Fahrenheit.
- The hero clip (shot 1, giant Wakka in Besaid village) next to the few
  lines that make it.
- Three steps with links: decompile, write a `partial` class, `dotnet build`
  and drop the package into Fahrenheit.
- The install commands, the latest release and the NuGet badges.
- Two facts that set it apart: all 1,621 scripts compile back byte for byte,
  and mods link onto the player's own game files, so a mod never ships a game
  file.

### Get started

- **Requirements.** The Steam HD Remaster, Fahrenheit, the .NET SDK, and where
  each comes from.
- **Install.** `dotnet tool install -g AtelSharp.Cli`,
  `dotnet new install AtelSharp.Templates`, the editor extensions.
- **Quickstart.** `atel decompile`, `dotnet new atel-mod`, build, install, see
  it in game: Wakka in Besaid village, twice his size. Five minutes, no
  explanation beyond what each command does.
- **Your first mod.** A tougher Dingo on the Besaid island path, one page per
  step with a "next" link. Written new from `tutorial.md`.

### Guides: the Besaid course

The guides teach on one island everyone knows and reaches about 1.5 hours into
the game: Besaid. Each guide adds one mod there, and each mod is a project in
`examples/Besaid/` that CI builds. The Larva examples stay in `examples/` for
the tests; the site doesn't use them.

| Guide | Example | What you see | Based on |
|---|---|---|---|
| Quickstart: change an actor when a map loads | `GiantWakka` | Wakka at twice his size in the village | language.md "Workers", "MapSetup" |
| First mod: a monster's stats and rewards | `ToughDingo` | Scan shows the new HP; the victory screen 999 gil | tutorial.md, language.md "Initial values and data edits" |
| Change what someone says | `BesaidGossip` | a villager's new line of dialogue | editor.md |
| Run code when you talk to someone (`[Postfix]` on `OnTalk`) | `DancingVillager` | the villager plays a motion after talking | language.md "Entries" |
| Shake the camera | `BeachQuake` | the camera shakes as Tidus steps onto the beach | language.md, research/oo-surface.md |
| Move faster | `FastTidus` | Tidus runs twice as fast on the island path | language.md |
| Keep data in the save (`[Saved]`) | `GenerousVillager` | a villager gives 1,000 gil, once per save | research/mod-saves.md |
| Add code inside a monster's turn (`[Inject]`) | `CleverFlan` | the Water Flan heals itself below half HP | language.md, plan.md Phase 9 |
| Count things across battles, read another mod's data | `BesaidTally` (+ a reader) | the F6 overlay counts Besaid battles, kept in the save | research/mod-saves.md |
| Print debug text in game | `WhereAmI` | the F6 overlay shows Tidus's position and the map, live | formats/debug.md |
| Switch a dead `if (false)` block on | (one found in Besaid, if any) | what the block does | sdk.md, research/dead-code.md |
| Find the script you want to change | | | research/handlers.md, corpus.md |
| Work next to a Fahrenheit code mod | | | sdk.md |
| Set up VS Code or Rider | | | editor.md |

Before any guide is written, each example is checked: the Besaid maps and the
monster scripts decompiled, the worker found (which one is Wakka, which one a
given villager), the mod built and seen working in game. An idea that doesn't
work in game is replaced by another visual one in Besaid, not dropped.

### Concepts

What a modder needs in their head before the reference makes sense:

- What ATEL is: the game's script machine, its scripts, workers and entries,
  and the 30 frames a second it runs at.
- Requests: how one worker asks another to do something, and waiting.
- Variables: script fields, save fields, registers, locals; which survive what.
- How a mod gets into the game: the package, the linker, the loader, and what
  happens when two mods change the same entry.
- What Atel# can't do: no .NET at run time, and the analyzer rules that say so.

### Reference

- **Language.** Which C# is Atel, written as a reference a person reads:
  per construct, what you write, what it does in game, what the analyzers
  refuse. Based on `language.md`.
- **Game library API.** Generated from `Ffx.Atel.Game`'s XML docs (7,115
  `<summary>` comments). `docfx metadata` with `outputFormat: markdown` writes
  one Markdown page per type into `site/src/content/docs/api/` at build time;
  if its output doesn't fit Starlight, a small tool in `tools/` reads the XML
  file and the assembly and writes the pages instead. The XML comments are the
  text of these pages, so this step includes a pass over the comments of the
  classes a modder uses most (`Btl`, `Actors`, `Camera`, `Text`, `Save`,
  `Wait`, `Debug`, the enums) to make them read well.
- **Analyzers.** One page per rule (`ATL0001` … `ATL4004`): what it means, an
  example that triggers it, the fix. Each analyzer gets a `HelpLinkUri` to its
  page, so the link in the IDE's error list opens the site. None set it today.
- **CLI.** One page per command, with the `--help` text generated at build
  time from the tool itself.
- **SDK properties** and **`mod.toml`.** Based on `sdk.md` and
  `formats/mod-package.md`.

### How it works

Three or four pages for the curious and for contributors: how the decompiler
turns bytecode into C#, how the compiler gets the same bytes back, how the
linker places a mod into a script it has never seen. Each is short, has a
diagram, and ends with links to `encoding.md`, `formats/`, `corpus.md` and
`research/` on GitHub.

### Project

The changelog, the releases, contributing, the MIT licence, and the line about
Fahrenheit names ("with the Fahrenheit project's permission", no person named).

## Code on the site

Code blocks are not typed into the pages. They are pulled from files at build
time (Starlight's `<Code>` component with a `?raw` import, or a remark plugin
that reads `// region` markers), from:

- `examples/` for mod code; CI already builds these projects;
- `site/snippets/`, a small C# project in the solution for snippets that
  aren't a whole mod (one construct of the language reference, an analyzer's
  bad and fixed example). It builds with the analyzers on; the "bad" examples
  are marked with the expected `ATL` code and a test checks each one raises it.

Vanilla decompiled source never appears, apart from short excerpts with `…`
as in the tutorial.

## The CLI as a .NET tool

`atel` is built from source today. With the next release it becomes the
package `AtelSharp.Cli` (`PackAsTool`, command `atel`), packed and published
by `release-sdk.yml` next to the SDK and the templates, MIT, the same version.
The Trusted Publishing policy on nuget.org covers it, as it covers any package
the workflow pushes for the `yutamago` account. The tutorial, `sdk.md` and the
README switch from "build the tools" to `dotnet tool install`.

## Build and deploy

A new workflow, `.github/workflows/pages.yml`:

1. **Build** on every PR and on `main`, filtered to `site/**`, `examples/**`,
   `src/Ffx.Atel.Game/**`, `src/Ffx.Atel.Analyzers/**` and
   `src/Ffx.Atel.Cli/**`.
   - `dotnet build` of `Ffx.Atel.Game` (XML docs), the CLI and
     `site/snippets/`; the API pages and the CLI `--help` pages.
   - `npm ci` and `astro build` in `site/`, with Starlight's link validation
     (`starlight-links-validator`), so a broken internal link fails the PR.
   - An external link check (lychee) runs weekly, not on every PR, so a slow
     outside site can't block a merge.
2. **Deploy** only on `main`: `upload-pages-artifact`, then `deploy-pages`
   with `pages: write` and `id-token: write`, environment `github-pages`.

Pages has to be switched to "GitHub Actions" once (repo settings, or
`gh api -X POST repos/yutamago/ffx-atel-sharp/pages -f build_type=workflow`).

One version of the site, built from `main`, with the latest release number in
the header. Versioned docs wait until a second release changes the API.

## Screenshots and clips

The user captures them in game; this list says what each one has to show.

For all of them:

- 1920×1080, English game text, default game settings, the FPS unlock overlay
  and the Fahrenheit window closed unless the shot is about them.
- Screenshots as PNG. Clips as MP4 (H.264), no sound needed, at most 15
  seconds; the site shows them muted and looping.
- Into `~/ffx-work/site-media/`, named by the number below (`01-hero.mp4`).
  They are reviewed there before any goes into the repo.

A save in Besaid village, after the first night (Wakka, the villagers and the
island path are all reachable), serves every game shot.

| # | Shot | Mod linked | What it shows | Used on |
|---|---|---|---|---|
| 1 | Clip, ~10 s | `GiantWakka` | Tidus walks up to a giant Wakka in the village | Home, hero |
| 2 | Screenshot | `GiantWakka` | the same, a still with both in frame | Quickstart |
| 3 | Screenshot | none | Scan on a Dingo: its vanilla HP | First mod: before |
| 4 | Screenshot | `ToughDingo` | the same, the new HP | First mod: after |
| 5 | Screenshot | `ToughDingo` | the victory screen with 999 gil | First mod |
| 6 | Screenshot | `BesaidGossip` | the villager's new line in the dialogue box | Text guide |
| 7 | Clip, ~8 s | `DancingVillager` | talking to the villager, then the motion | `OnTalk` guide |
| 8 | Clip, ~8 s | `BeachQuake` | Tidus steps onto the beach, the camera shakes | Camera guide |
| 9 | Clip, ~10 s | `FastTidus` | Tidus running on the island path, twice as fast | Speed guide |
| 10 | Screenshot | `GenerousVillager` | the "Obtained 1,000 gil" message | `[Saved]` guide |
| 11 | Screenshot | `GenerousVillager` | talking again: the villager's "already gave" line | `[Saved]` guide |
| 12 | Clip, ~10 s | `CleverFlan` | a Water Flan below half HP heals itself | `[Inject]` guide |
| 13 | Screenshot | `BesaidTally` + reader | the F6 overlay after a few battles, both mods' lines | Tally guide |
| 14 | Screenshot | `WhereAmI` | the F6 overlay with Tidus's position and the map name | Debug guide |
| 15 | Screenshot | any | Fahrenheit's mod list with `yuta.atel` and the Atel# mod loaded | Install, Quickstart |
| 16 | Screenshot | an error on purpose | Rider or VS Code showing an `ATL` error with its message and the help link | Analyzers reference |
| 17 | Screenshot | none | VS Code with a Besaid script open and a message's text shown next to its id | Editors guide |

Each shot waits until its example works in game; then I link the mods and
launch the game for it. The list changes if an example is replaced.

## Steps

1. **Skeleton.** `site/` with Starlight, the navigation, `pages.yml`, Pages
   switched on, a placeholder home page, README and repo homepage link. Done
   when the site is live and builds on every PR.
2. **CLI tool.** `AtelSharp.Cli` packed and published by the release
   workflow; released as `0.1.0-alpha.2`.
3. **Besaid examples.** Decompile the Besaid maps and the Dingo, Condor and
   Water Flan scripts, find the workers, write the mods in `examples/Besaid/`,
   check each in game. Replace what doesn't work.
4. **Get started.** Requirements, install, the Quickstart, the first mod.
   Uses shots 1 to 5 and 15.
5. **Reference.** The API pages and the pass over the most-used XML comments,
   one page per analyzer rule with `HelpLinkUri` set, the CLI pages, the
   language reference, `site/snippets/`.
6. **Concepts and guides.** The concept pages, one page per guide. Uses shots
   6 to 14, 16 and 17.
7. **How it works.** The overview pages with their diagrams.
8. **Home and polish.** The real home page with shot 1, a social preview image,
   a 404 page, a check at phone width and in light and dark.

Each step is one PR with CI green and `origin/main` merged in. Steps 2, 3, 5
and 7 can run in parallel once step 1 is merged; 4 and 6 need the examples
from 3.

## Decisions

The user, 2026-11-10:

1. Generator: Astro Starlight.
2. URL: `yutamago.github.io/ffx-atel-sharp` for now.
3. The CLI becomes a .NET tool, `AtelSharp.Cli`.
4. The site is for people: its pages are written new, well written and easy to
   follow, with links to `docs/` in the repo for the details. `docs/` is not
   rendered on the site.
5. Screenshots and clips: the plan lists what's needed, the user captures them.
6. The examples on the site are visual and in a beginner area: the Besaid
   course replaces the Larva (a rare monster reached after about 20 hours).
