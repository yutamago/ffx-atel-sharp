<!-- SPDX-License-Identifier: MIT -->
# AtelSharp.Cli

`atel`, the command line tool of [Atel#](https://github.com/yutamago/ffx-atel-sharp). It decompiles the
event, battle and monster scripts of Final Fantasy X (the Steam HD Remaster) to C#, compiles C# back to
the game's ATEL bytecode, and builds and links mods for the Atel loader, a Fahrenheit mod.

```bash
dotnet tool install -g AtelSharp.Cli --prerelease
export FFX_GAME_DIR=<the folder with FFX.exe>
atel decompile m209 -o vanilla
```

`atel --help` lists the commands. `atel` reads the game's files from your own copy of the game and never
writes into the game folder. The projects `atel decompile` writes use the `AtelSharp.Sdk` package of the
same version as the tool. Needs .NET 10.

See `docs/tutorial.md` and `docs/sdk.md` in the repository.
