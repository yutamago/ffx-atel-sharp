---
title: Requests
description: How one worker asks another to run an entry, how it waits for it, and how priority levels let entries interrupt each other.
sidebar:
  order: 2
sources:
  - "docs/language.md#requests"
  - "docs/language.md#parameters-and-results"
  - "docs/language.md#entries"
  - "docs/language.md#workers"
  - "docs/research/handlers.md#the-table"
---

In a cut scene, one worker usually directs. It asks Wakka's worker to walk to the beach, waits until he is there, then asks for the next line of dialogue. Asking another worker to run one of its entries is a request. It is the only way workers start each other's code.

## Writing a request

An entry that other workers can request is a static `async` method that returns a `Task`, such as `[Entry(5)] public static async Task WalkAsync()` in Wakka's worker class.

The requesting worker calls it like a method, and how it calls it says whether it waits:

| You write | What happens |
|---|---|
| `await Wakka.WalkAsync();` | Wakka runs the entry. The caller waits until it has ended. |
| `_ = Wakka.WalkAsync();` | Wakka gets the request. The caller carries on at once. |
| `await Wakka.WalkAsync().Started;` | The caller waits only until Wakka has begun the entry. |

The `Task` here is Atel#'s own, `Ffx.Atel.Task`, not the .NET type. There are no threads behind it. `await` compiles to one of the machine's wait instructions, and the caller simply lets frames pass until the other worker is done. The C# reads the way the game behaves: you ask, the other worker does it in its own frames, and you go on when it has finished.

Requests are written as calls of static methods, `Wakka.WalkAsync()`, because each worker class has only one instance. You never create or hold a worker.

## Priority levels

A worker runs one request at a time on each of its priority levels. A request on a higher level interrupts whatever the worker is doing on a lower one. When the higher one ends, the lower one goes on from where it stopped.

The old woman in Besaid village shows this. Her `Main` runs on the lowest level and walks her around the village. When you talk to her, the game requests her `OnTalk` on a higher level. Her walk stops where it is, she talks, and when `OnTalk` ends, `Main` picks up the walk again. Neither entry has to know about the other.

Scripts use levels 1 to 6, and a request without a level is on level 1. To pass one, the entry declares a `Priority` parameter and the caller writes `_ = Wakka.WalkAsync(Priority.P3);`. The game itself uses levels 0, 7 and 8 for `Main`, `Init` and the battle, and the analyzers warn when new code uses those.

## The game makes requests too

`Init`, `Main` and `OnTalk` run because the game requests them. The battle does the same with a monster. When a Dingo's turn comes, the battle looks up which worker and entry handle "its turn" in the Dingo's script and requests that entry, `Ai.OnTurn`, on a high level. It then waits until the worker has finished, so the Dingo has picked its attack before the battle goes on.

That is why a mod that adds code to `OnTurn` changes what the Dingo does on every turn, without any code of its own to start it.

## Waiting on your own worker

A worker can request one of its own entries, but awaiting it only works when the request's level is above the level the caller runs on. On the same level or a lower one, the entry can't start until the caller has finished, and the caller is waiting for the entry. Either the wait ends at once with nothing done, or it never ends. The analyzers refuse the cases they can see, and warn about the rest.

## Passing values

A requested entry can take parameters and return a value, as a C# method does: `int left = await Ai.DamageAsync(100);`.

The machine's requests carry no values, only "which worker, which entry, which level". So the compiler keeps each parameter and the result in hidden fields of the receiving worker: the request writes the arguments, then requests the entry, and `await` reads the result once the entry has ended.

Because those fields belong to the entry, not to one request, two requests to the same entry that overlap see each other's values. Request such an entry once at a time and await it. The analyzers warn where requests may overlap.

## Patches can't await

A mod's `[Prefix]`, `[Postfix]` or `[Inject]` method runs inside a vanilla entry. It is not an entry of its own, so it can't `await` a request. It can still call anything that waits by itself, such as `Wait.Frames(30)` or `Motion.Wait()`. When a patch needs to wait for a request, give the worker a new entry of your own, request it from the patch with `_ =`, and await inside that entry.

## Details

The request forms, the levels and the rules for parameters are in [docs/language.md, "Requests"](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/language.md#requests). How the battle picks the worker for each event is in [docs/research/handlers.md](https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/research/handlers.md).
