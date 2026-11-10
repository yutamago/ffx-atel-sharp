<!-- SPDX-License-Identifier: MIT -->
# AtelSharp.Templates

`dotnet new` templates for [Atel](https://github.com/yutamago/ffx-atel-sharp) mods: C# that compiles to the
ATEL bytecode of Final Fantasy X (the Steam HD Remaster), for mods loaded through Fahrenheit.

```bash
dotnet new install AtelSharp.Templates
dotnet new atel-mod -n MyMod --script m209
```

`atel-mod` writes a project of the `AtelSharp.Sdk` (of this package's version, in `global.json`) that changes
one script, a `nuget.config` naming a local folder with the SDK package, and a README on decompiling the
vanilla source and building. Options: `--script` (the script's file name: `m209`, `kami0000`,
`kami00_00`), `--authors`, `--mod-id`, `--vanilla` (the vanilla source folder), `--feed` (the local package
folder), `--sdk-version`, `--license` (`MIT`, the default: a `LICENSE` with the year and `--authors`, SPDX
headers and `PackageLicenseExpression`; or `none`).

See `docs/sdk.md` in the repository.
