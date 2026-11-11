---
title: From bytecode to C#
description: How the decompiler turns a script the game ships into C# that reads like code a person wrote.
sidebar:
  order: 1
sources: ["docs/encoding.md#control-flow", "docs/encoding.md#what-the-decompiler-checks", "docs/plan.md#phase-3-statements", "docs/plan.md#phase-4-control-flow", "docs/plan.md#phase-5-typing-and-naming", "docs/plan.md#phase-6-source-layout", "docs/research/oo-surface.md", "docs/corpus.md"]
---

The game doesn't ship the source of its scripts. It ships bytecode: instructions for a small stack machine, with numbers where the source had names. The decompiler's job is to turn that back into C# you can read, without losing a single byte on the way.

<figure class="diagram">
<svg viewBox="0 0 540 250" role="img" aria-labelledby="dec-title">
<title id="dec-title">Seven bytecode instructions on the left, and the four lines of C# they become on the right.</title>
<defs><marker id="dec-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path class="head" d="M0,0 L10,5 L0,10 z"/></marker></defs>
<text x="10" y="22" class="label">Bytecode</text>
<text x="310" y="22" class="label">C#</text>
<rect class="box" x="10" y="34" width="220" height="206" rx="6"/>
<rect class="accent" x="310" y="34" width="220" height="206" rx="6"/>
<text x="24" y="62" class="mono">pushv v3</text>
<text x="24" y="86" class="mono">pushimm 5</text>
<text x="24" y="110" class="mono">lt</text>
<text x="24" y="134" class="mono">popa_jz l0</text>
<text x="24" y="166" class="mono">pushimm 30</text>
<text x="24" y="190" class="mono">func_facc WaitFrames</text>
<text x="24" y="222" class="mono">l0:</text>
<text x="324" y="86" class="mono">if (V3 &lt; 5)</text>
<text x="324" y="118" class="mono">{</text>
<text x="348" y="150" class="mono">Wait.Frames(30);</text>
<text x="324" y="182" class="mono">}</text>
<path class="line" d="M232,98 C270,98 270,82 306,82" marker-end="url(#dec-arrow)"/>
<path class="line" d="M232,178 C270,178 270,146 306,146" marker-end="url(#dec-arrow)"/>
<path class="line dashed" d="M232,218 C270,218 270,178 306,178" marker-end="url(#dec-arrow)"/>
</svg>
<figcaption>Each statement is the code between two points where the stack is empty. The conditional jump and its label become an <code>if</code>.</figcaption>
</figure>

## Statements

ATEL works on a stack: an instruction pushes values, the next one takes them off and computes. In every script the game ships, the stack is empty between two statements of the original source. So the decompiler cuts the code at every point where the stack is empty, and each piece becomes one C# statement.

It then checks each statement by compiling it again. A statement is kept only if it gives back exactly the bytes it came from. This check runs everywhere in the decompiler, and it is why the result can be trusted.

## Control flow

The original compiler wrote each `if`, `while`, `for` and `switch` as a fixed pattern of jumps and labels. The decompiler looks for those patterns and writes the C# construct each one came from. In the diagram, the conditional jump over the call and the label after the call are an `if`.

After a whole worker is structured, the decompiler compiles it again and compares it with the original. A construct that doesn't come back the same is dropped, and the worker is structured again without it. A few jumps have no C# form, for example a jump out of several nested loops to one shared exit. Those stay as `goto`, which C# has too.

Some code in the game can never run: the original jumps over it. The decompiler keeps it as `if (false) { … }`, because removing it would change the bytes. Some of these blocks are cut content, and a mod can switch a named one back on.

## Names

The bytecode says "variable 3", "message 12" or "story progress 1340". The game library Atel# ships with, the reference assembly, holds the names for these numbers, and the decompiler writes the name wherever it knows one: `Text[12]` for a message, `Story.S01340_Kami0400` for a point in the story, a save field's name, a monster or a battle command from an enum.

Every name compiles to exactly the number it replaces, so names never change the bytes. A name comes from a verified source: the scripts themselves (a point in the story is named after the scripts that reach it), the game's code, or the ID tables of the Fahrenheit project, used with the Fahrenheit project's permission. Where nobody knows what a value means yet, it stays a number.

## Members

Most of what a script does is call the game's built-in functions, called natives. Many of them act on the worker that runs the code: set its position, play a motion, turn it. The decompiler writes those as members of the worker, so the source reads like object-oriented C#: `Pos = new Vector3(…)`, `Motion.Play(…)`, `Turn.Start(…)`. A native that acts on another worker is a member of that worker's class, as in `Tidus.Pos.X`.

Underneath, each member is the same native call. The compiler reads `Motion.Play(…)` and writes the call the game expects.

Code that repeats across many scripts, with only its values changed, is written once as a method marked `[Inline]`. The call compiles to the same instructions the original had in that place.

## The files

A script becomes a folder and a namespace, with one file for the script class and one file per worker. The scripts of one map area form one C# project, so an editor can open an area at a time. This is the vanilla source your mod is built against.

## Details

- [How each construct compiles, and what the decompiler checks](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/encoding.md#what-the-decompiler-checks) (docs/encoding.md)
- [The decisions behind statements, control flow, names and layout](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/plan.md#phase-3-statements) (docs/plan.md, phases 3 to 6)
- [The gotos that stay, and why](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/plan.md#phase-4-control-flow) (docs/plan.md)
- [The object-oriented members, how they were chosen](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/research/oo-surface.md) (docs/research/oo-surface.md)
- [The shipped scripts, measured](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/corpus.md) (docs/corpus.md)
- [The dead `if (false)` blocks](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/research/dead-code.md) (docs/research/dead-code.md)
