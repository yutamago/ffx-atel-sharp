---
title: The same bytes back
description: How the compiler turns C# into bytecode, and why it gives back exactly the bytes the game ships.
sidebar:
  order: 2
sources: ["docs/encoding.md", "docs/encoding.md#new-code", "docs/corpus.md#what-the-compiler-can-derive-and-what-must-be-pinned", "docs/formats/script-block.md", "docs/plan.md#phase-2-the-source-shape"]
---

The compiler reads C# with Roslyn, the C# compiler's own parser, and writes the bytecode for it. Give it the decompiled vanilla source of a script, and it writes the script back: not a script that behaves the same, but the same bytes the game ships. This holds for every script in the game.

<figure class="diagram">
<svg viewBox="0 0 540 250" role="img" aria-labelledby="cmp-title">
<title id="cmp-title">The game's script is decompiled to vanilla source, the source is compiled again, and the result is compared with the game's script byte for byte.</title>
<defs><marker id="cmp-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path class="head" d="M0,0 L10,5 L0,10 z"/></marker></defs>
<rect class="box" x="10" y="20" width="190" height="64" rx="6"/>
<text x="105" y="48" text-anchor="middle" class="label">The game's script</text>
<text x="105" y="70" text-anchor="middle" class="small">bytecode</text>
<rect class="accent" x="340" y="20" width="190" height="64" rx="6"/>
<text x="435" y="48" text-anchor="middle" class="label">Vanilla source</text>
<text x="435" y="70" text-anchor="middle" class="small">C#</text>
<rect class="box" x="10" y="166" width="190" height="64" rx="6"/>
<text x="105" y="194" text-anchor="middle" class="label">Compiled script</text>
<text x="105" y="216" text-anchor="middle" class="small">bytecode</text>
<path class="line" d="M202,52 L336,52" marker-end="url(#cmp-arrow)"/>
<text x="270" y="42" text-anchor="middle">decompile</text>
<path class="line" d="M435,86 L435,198 L204,198" marker-end="url(#cmp-arrow)"/>
<text x="320" y="188" text-anchor="middle">compile</text>
<path class="line dashed" d="M105,86 L105,164"/>
<rect class="accent" x="70" y="110" width="70" height="30" rx="15"/>
<text x="105" y="131" text-anchor="middle" class="label">equal</text>
<text x="152" y="131" class="small">compared byte for byte</text>
</svg>
<figcaption>The round trip. It runs over every script the game ships, and any change to Atel# that alters one byte fails it.</figcaption>
</figure>

## Why the same bytes

When the source compiles back to the shipped bytes, the vanilla source is the game. Nothing in it is a guess or an approximation: every `if`, every number and every call is what the game runs. When you read a method to decide where your mod goes, you are reading the real thing.

It also makes your mod's effect exact. A mod that adds a postfix to one method changes that method and nothing else, because the rest of the script is still the game's own bytes.

And it keeps Atel# honest. A rule that is slightly wrong shows up as a byte that differs, in some script, the next time the round trip runs.

## The original compiler's habits

The compiler the game's scripts were first built with made many small choices that the bytecode doesn't need, and Atel# has to make the same ones. A few examples:

- A small number is pushed with one instruction, a larger one comes from the script's table of constants. The constants in that table are in the order the code first uses them.
- A `for` loop puts its step before its body in the bytecode, not after it.
- Jumps forward and jumps backward use two different conditional jump instructions.
- The labels of each worker are numbered in the order the constructs around them start, not in the order they appear.

Each habit is a rule in the compiler, written down in the encoding reference. Every form of C# the decompiler writes has exactly one encoding, so there is only one way for a statement to come out.

## Where the original broke its own rules

In a few scripts, the original compiler didn't follow its habits. A constant table is in another order, or a method's labels are numbered differently. For those, the decompiler writes an attribute that pins the choice, such as `[Ints(…)]` for a constant table or `[Labels(…)]` for a method's labels. You will see a few of these attributes in the vanilla source. Leave them as they are; they are what makes that script come back the same.

## New code

Your mod can use C# the original compiler never wrote: `&&` and `||` that skip the right side, `x++` inside an expression, `?:`, local variables, helper methods. These have encodings of their own. The round trip doesn't constrain them, because no shipped script has them, but they follow the same rules about the stack, so the game runs them like any other code.

## Details

- [How each construct compiles to bytecode](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/encoding.md) (docs/encoding.md)
- [What the compiler derives, and what is pinned](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/corpus.md#what-the-compiler-can-derive-and-what-must-be-pinned) (docs/corpus.md)
- [The script block, byte for byte](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/formats/script-block.md) (docs/formats/script-block.md)
- [The encoding of new code](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/encoding.md#new-code) (docs/encoding.md)
- [The source shape and its decisions](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/plan.md#phase-2-the-source-shape) (docs/plan.md)
