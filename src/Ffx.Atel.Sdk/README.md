<!-- SPDX-License-Identifier: MIT -->
# AtelSharp.Sdk

An MSBuild project SDK for [Atel](https://github.com/yutamago/ffx-atel-sharp): C# that compiles to the
ATEL bytecode of Final Fantasy X (the Steam HD Remaster), for mods loaded through Fahrenheit.

```xml
<Project Sdk="AtelSharp.Sdk/<version>">
    <PropertyGroup>
        <AtelVanillaSource>../vanilla</AtelVanillaSource>
    </PropertyGroup>
</Project>
```

`dotnet build` checks the mod's C# against the decompiled vanilla source (the analyzers run in the IDE
and on build) and writes the Fahrenheit mod folder, `bin/<configuration>/net10.0/mods/<id>/`, with
one package per script the mod changes. Needs `FFX_GAME_DIR` (the game folder) and .NET 10.

The package also carries the `atel` command line tool (`tools/atel/atel.dll`, run with `dotnet`), which its
targets run. To use `atel` yourself, install the .NET tool of the same version:
`dotnet tool install -g AtelSharp.Cli --prerelease`.

See `docs/sdk.md` in the repository.
