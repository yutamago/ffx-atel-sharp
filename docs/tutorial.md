<!-- SPDX-License-Identifier: MIT -->

# Your first mod: a tougher Larva

This tutorial writes a mod that gives the Larva (monster `m209`, Thunder Plains) 1234 HP instead of
its 1498, builds it, installs it into Fahrenheit and checks it in battle. It assumes you know C#;
the language is described in [language.md](language.md).

The mod is a few lines of C# against the decompiled source of your own game. What it ships is a
package of 9 bytes of code plus references to the vanilla script by number and hash: no game code.
The loader links it onto the player's own file when the game starts.

## 1. What you need

- **FFX** from the Steam *FINAL FANTASY X/X-2 HD Remaster*.
- **[Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit)**, the FFX mod loader, installed as
  its README says, so that the game starts through it and reads mods from its `mods/` folder. The
  Atel loader was tested with Fahrenheit 1.0.0-alpha12.
- **The .NET 10 SDK** (`dotnet --version` shows `10.…`).
- A checkout of this repository.

## 2. Build the tools

From the repository's root:

```bash
dotnet build -c Release src/Ffx.Atel.Cli      # the atel command
dotnet build -c Release src/Ffx.Atel.Loader   # the Fahrenheit mod yuta.atel
```

The command is `artifacts/bin/Ffx.Atel.Cli/release/atel`; the loader is written to
`artifacts/deploy/release/mods/yuta.atel/`. For the rest of the tutorial:

```bash
alias atel="$PWD/artifacts/bin/Ffx.Atel.Cli/release/atel"
export FFX_GAME_DIR=<the folder with FFX.exe>     # atel reads data/FFX_Data.vbf from it
```

`atel` reads the game's files directly from `FFX_Data.vbf`; it never writes into the game folder.
(`--game <dir>` works instead of `FFX_GAME_DIR`.)

## 3. Decompile the Larva

Work in a folder of your own, outside this repository (decompiled source is game code: keep it on
your machine).

```bash
mkdir -p ~/ffx-mods && cd ~/ffx-mods
atel decompile m209 -o vanilla
```

```
1 scripts in 1 projects; 0 workers derive from 0 library classes, 0.0 MB of source saved; dotnet build vanilla/Atel.slnx
```

`vanilla/Monsters/M209/` now holds the script: `M209.cs` (the script class: its workers
`W00`-`W02` and its variables), `M209.Data.cs` (its data areas) and one file per worker.
`dotnet build vanilla/Atel.slnx` compiles it as C#, and any C# IDE opens it.

Open `vanilla/Monsters/M209/W01.cs`. Worker 1 is the Larva's battle worker (a `Worker2`, as every
monster worker), and its `Init` is entry 0, which the engine runs when the monster is set up:

```csharp
public sealed partial class W01 : Worker2
{
    protected override void Init()
    {
        Btl.SetActorProperty(…);
        …
        return;
    }
    …
}
```

`Init` sets the monster up from its data; the HP comes from the monster's stats. Setting actor
property 0 (`Btl.SetActorProperty(0, value)`, native `0x70ab`) after that replaces the HP, as found
in game ([research/loader.md](research/loader.md#notes)).

## 4. Write the mod

A mod is a folder of C# files, separate from the vanilla source. Each file is in the namespace of
the script it changes and extends its classes with `partial`.

```bash
mkdir larva-hp
```

`larva-hp/LarvaHp.cs`:

```csharp
using Ffx;
using Ffx.Atel;
using static Monsters.M209.M209;

namespace Monsters.M209;

partial class W01
{
    /// <summary>After the vanilla Init: set the Larva's HP (actor property 0).</summary>
    [Postfix(nameof(Init))]
    void MoreHp()
    {
        Btl.SetActorProperty(0, 1234);
    }
}
```

- `partial class W01` adds to the vanilla worker `W01`. A mod does not repeat its attributes or base
  class.
- `[Postfix(nameof(Init))]` runs the method after the vanilla `Init`, wherever `Init` ends the
  entry. `[Prefix(…)]` runs before it, `[Replace(…)]` instead of it; a method without an attribute
  would be a new entry ([language.md](language.md#mods)).
- The `using static` line is what every decompiled file has: it makes the script's variables
  visible. This mod uses none, but keep it.

Optionally, `larva-hp/mod.toml` declares how the mod relates to others; every key is optional
([formats/mod-package.md](formats/mod-package.md#a-mod-on-disk)):

```toml
after = []
requires = []
incompatible = []
```

### In an IDE (until the SDK lands)

`atel compile-mod` needs no project file. To get completion and C# errors in an IDE, put a project
file next to the mod that pulls in the vanilla script, `larva-hp/LarvaHp.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <!-- The decompiled tree's settings and its reference assembly. -->
    <Import Project="../vanilla/Directory.Build.props" />
    <ItemGroup>
        <!-- The vanilla script the mod changes, so that the partial classes resolve. -->
        <Compile Include="../vanilla/Monsters/M209/**/*.cs" Link="vanilla/%(RecursiveDir)%(Filename)%(Extension)" />
    </ItemGroup>
</Project>
```

`dotnet build larva-hp` then reports C# errors. It does not run the Atel analyzers; `atel
compile-mod --check` does (next step). `compile-mod` ignores the project file and the `obj/` and
`bin/` folders. Keep the mod folder outside `vanilla/`: the decompiled tree's own
`Directory.Build.props` would apply twice, and `atel compile` of the vanilla script would read the
mod's files as part of it.

## 5. Build the mod package

### Today: `atel compile-mod`

```bash
atel compile-mod larva-hp --vanilla vanilla -o out/my.larva-hp --id my.larva-hp --check
```

```
m209: 1 entries and patches, 0 new workers, 0 new variables -> out/my.larva-hp/atel/ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin.json
1 script package(s) in out/my.larva-hp
```

- `--vanilla` is the folder `atel decompile -o` wrote; the mod's namespace (`Monsters.M209`) finds
  the script in it.
- `--check` first compiles the vanilla source and the mod together as C#, with the Atel analyzers,
  and stops on errors.
- `--id` names the mod and writes a Fahrenheit manifest (`my.larva-hp.manifest.json`, depending on
  `yuta.atel`) if the output folder has none. Edit its `Name`, `Desc`, `Authors` and `Version` as
  you like; the id must stay the folder's name.

`out/my.larva-hp/` is the whole mod:

```
my.larva-hp.manifest.json
mod.toml                                                     (if the mod has one)
atel/ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin.json  the package for m209
```

The package lists the vanilla parts it attaches to (worker 1, the method `Init`, each with a hash)
and the postfix's code. Nothing in it comes from the game's files.

To see what the loader will make of it, link it here as the loader does:

```bash
atel link out/my.larva-hp -o linked
```

```
ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin: my.larva-hp
info: my.larva-hp: m209: worker 1's Init: postfix MoreHp
```

`linked/ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin` is the linked monster file (56,624 bytes;
the vanilla one is 55,280). This step is only a check; the loader links on its own at game start.

### With the SDK: `dotnet build`

> **To be updated when `Ffx.Atel.Sdk` is merged.** The SDK package (plan, Phase 8) is meant to make
> `dotnet build` of a mod project do what `atel compile-mod` does, with the analyzers in the IDE.
> This section will then replace the project file of step 4 and the command above.

## 6. Install

Into Fahrenheit's `mods/` folder (`<Fahrenheit>/mods/`):

1. **The loader**, once: copy `artifacts/deploy/release/mods/yuta.atel/` from this repository's
   build to `<Fahrenheit>/mods/yuta.atel/`.
2. **The mod**: copy `out/my.larva-hp/` to `<Fahrenheit>/mods/my.larva-hp/`. The folder's name is the
   mod's id.
3. **The load order**: `<Fahrenheit>/mods/loadorder` lists the enabled mods, one id per line. Add
   both, the loader first:

   ```
   yuta.atel
   my.larva-hp
   ```

   The loader links the Atel mods (every listed mod with an `atel/` folder) in this order; mods
   later in the list patch after earlier ones.

The game folder and `FFX_Data.vbf` stay untouched. To disable the mod, remove its line from
`loadorder`: the loader empties its output folder at every start and links only what is listed.

## 7. Run and check

Start the game through Fahrenheit. At start the loader links the mod onto your `m209.bin`, writes
the result to `<Fahrenheit>/mods/yuta.atel/efl/x/`, and Fahrenheit serves it in place of the
vanilla file. Fahrenheit's log shows:

```
Atel loader: Atel mods in load order: my.larva-hp; 1 file(s) linked.
Atel loader: info: my.larva-hp: m209: worker 1's Init: postfix MoreHp
Atel loader: serving linked ffx_ps2/ffx/master/jppc/battle/mon/_m209/m209.bin
Atel loader: CDfileSize_CD hooked for 1 served file(s).
```

and Fahrenheit's own line that mod Atel replaces `…\_m209\m209.bin`.

Then fight Larvas on the Thunder Plains (battle 34, `kami00_00`, is one of their formations). A
Larva now has 1234 HP. Sensor or Scan shows a monster's HP in battle. This was checked in game: in
battle 34 the Larva's HP read from the game's memory was 1234, and its script block in memory was
the linked block byte for byte ([research/loader.md](research/loader.md#phase-9-a-linked-mod-package-in-game)).

If something is wrong, the log says what:

| Log | Meaning |
|---|---|
| `Atel loader: no Atel mods in the load order.` | `my.larva-hp` is not in `loadorder`, or its folder has no `atel/` |
| `Atel loader: error: my.larva-hp: … differs from the one the mod was built against (hash mismatch)` | your `m209.bin` is not the file the mod was built against (another game version, or a mod built on edited vanilla); the package is left out and the vanilla script runs |
| `Atel loader: no scripts built: …` | the loader failed as a whole; the game runs with the vanilla scripts |

## 8. Next steps

- Change something else: another vanilla method (`[Prefix(nameof(E2))]` on the turn logic), a new
  variable in `partial class M209 { public static int Turns; }`, a new entry another method
  requests (`_ = W01.CountAsync();`). [language.md](language.md) lists what compiles today.
- Read a few decompiled scripts: `atel decompile <script> -o vanilla` for any event, monster or
  battle script (`atel list` lists them), and `atel text export <script> -o vanilla` with the
  [VS Code extension](editor.md) to see the messages behind `Text[n]`.
- How the source maps to bytecode: [encoding.md](encoding.md).
