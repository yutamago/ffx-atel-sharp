---
title: What Atel# can't do
description: Why there is no .NET at run time, what the analyzers refuse and why, and where a script mod ends.
sidebar:
  order: 5
sources:
  - "docs/language.md#the-machine-in-brief"
  - "docs/language.md#types-and-values"
  - "docs/language.md#workers"
  - "docs/language.md#mods"
  - "docs/analyzers.md#numbering"
---

Try `var names = new List<string>();` in a mod and the IDE underlines it in red before you finish typing. The code is valid C#, but the game could never run it. This page explains why, and what else is out of reach.

## No .NET at run time

Atel# uses C# as its language, not .NET as its runtime. The compiler turns your C# into ATEL bytecode, the same kind of code the game's own scripts are made of, and that bytecode runs on the game's script machine. No .NET runtime is involved when the game runs your mod.

The script machine is small. It has whole numbers, floating-point numbers, variables in a few fixed places, and calls to the game's own functions. It has no objects, no strings, no memory to allocate and no exceptions. So whatever C# would need those for can't run:

- `new` at run time: objects, collections, and arrays beyond the fixed-size ones a variable declares. LINQ goes with them.
- `string`, except in the text of `Debug.Print` and `Text.New`, which the compiler stores apart from your code.
- The .NET base library: `Math`, `Console`, `DateTime`, `Random` and the rest. The game's own functions take their place, such as `Common.Random16` for a random number.
- Exceptions, lambdas, delegates and generic methods.
- `long`, `double` and `decimal`. Numbers are 32-bit `int` and `float`, so write `1.5f`, not `1.5`.

What you do get covers what scripts do: `if`, `switch`, loops, arithmetic, locals, the game's functions, and [requests](/ffx-atel-sharp/concepts/requests/) between workers. `async` and `await` work, because they map onto the machine's own waiting.

## The analyzers

Atel# ships Roslyn analyzers, so Rider, Visual Studio and VS Code show these rules while you type, and `dotnet build` fails on them. You find out in the editor, not when the game crashes. The rules come in four groups:

- C# the machine can't run: everything in the list above.
- [Requests](/ffx-atel-sharp/concepts/requests/): requesting an entry that isn't `async`, awaiting your own worker's entry where it can never start, using the priority levels the game keeps for itself, and requests to an entry with parameters that may overlap.
- Calls between workers. A function worker runs on its caller's worker, and the machine allows only three such calls inside each other, with no recursion. `Wait.Seconds` needs a constant of at least one frame, because the compiler counts the frames.
- What a mod writes on top of vanilla: a static constructor that does more than set starting values, an `[Inject]` whose statement isn't in the method, a `[Saved]` field of a type that can't be saved, and a write to another mod's saved field.

One more rule is a warning, not an error. It flags a [register](/ffx-atel-sharp/concepts/variables/#registers) that an entry uses across a wait while another entry of the same worker writes it. Vanilla scripts do this in places, on purpose or not, so the warning shows up in vanilla source too.

Each rule has an id starting with `ATL`. The analyzer reference in the [Reference](/ffx-atel-sharp/reference/) section will have one page per rule, with an example that triggers it and the fix.

## A worker holds only entries

A worker class holds only its entry methods and its register fields. A mod can't add properties or ordinary methods to it, and it can't change the attributes or base class of a vanilla class. A plain method in your part of a worker, with no attribute, becomes a helper that the compiler turns into a function worker of the mod. Starting values for a worker's private data are only possible in map scripts, because in battle the game clears that data when it sets the worker up.

## Scripts, not the engine

Atel# changes what the scripts do: who stands where, what they say, how a monster picks its moves, a monster's stats, what a battle does when it starts. It can only use what the game offers scripts.

Much of the game isn't script. The menus, the damage formulas, how the game draws, how fast it runs: all of that is code in the game's executable. To change it you need a Fahrenheit code mod, written in ordinary .NET, which can run next to your Atel# mod.

## Details

Every rule with its examples is in [docs/analyzers.md](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/analyzers.md). Which C# compiles to what is in [docs/language.md](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/language.md).
