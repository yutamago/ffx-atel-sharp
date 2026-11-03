<!-- SPDX-License-Identifier: MIT -->

# Atel for Unity people

You know C# from Unity: `MonoBehaviour`, `Start()`, `Update()`, coroutines with `yield return`,
`GetComponent`. This page maps what you know onto FFX's scripts as the decompiled source shows them,
so you can read a scene in the source repo and change it in a mod. The language itself is described
in [language.md](language.md); the first mod is in [tutorial.md](tutorial.md). All the code below is
made up for this page.

## The big picture

| Unity | Atel |
|---|---|
| a scene | a **script**: one map's events (`kami0000`, the Thunder Plains' south end), a battle (`kami00_00`) or a monster (`m209`) |
| a `GameObject` with a script component | a **worker**: a class in the script's folder (`Tidus.cs`, `W18.cs`) |
| `Start()` | `protected override void Init()`, entry 0: runs once when the map loads |
| `Update()` | `protected override void Main()`, entry 1: runs once, then usually waits for ever or loops itself |
| a coroutine | an **entry**: `public static async Task E6Async()`, run when another worker *requests* it |
| `StartCoroutine(x)` | `_ = Lulu.E6Async();` (start it and go on) |
| `yield return StartCoroutine(x)` | `await Lulu.E6Async();` (wait until it has run) |
| `yield return new WaitForSeconds(0.5f)` | `Common.WaitFrames(15);` (frames, not seconds) |
| `Debug.Log` | `Debug.Print($"hp = {Hp}");` (printed by the Atel loader, [formats/debug.md](formats/debug.md)) |
| a static field | a variable of the script class: `[Var(Storage.Event, 0x0012)] public static byte V3;` |
| a `ScriptableObject` holding text | the script's message table: `Text[71]` is message 71 |

There is no `new`, no `string` outside `Debug`, no lists, no LINQ, no lambdas: the game's script
machine (ATEL) runs integers, floats and calls into the engine ("natives", such as
`Common.MsgSetText`). The analyzers tell you when you write something it cannot run.

## A worker

```csharp
public sealed partial class Guard : Worker1      // Worker1: an actor (a model on the map)
{
    protected override void Init()                // when the map loads
    {
        Common.SetModel(Model.GaGatta216);        // its model
        Common.SetPosition(10f, 0f, -40f);
    }

    protected override void Main()                // then
    {
        Common.WaitForever();                     // nothing to do until someone asks
    }

    [Entry(2)]
    void Talk()                                   // entry 2, run when something requests it
    {
        Dialogue.Say(Text[12]);                   // show message 12 and wait until it is read
    }
}
```

Every worker runs on its own, like a coroutine per object, and all of them run each frame. A
call to a native that takes time (`WaitFrames`, `MsgWaitEnd`, `MoveWait`) suspends the worker
until it is done, as `yield return` would.

`[Entry(n)]` gives the number the game and the other workers run the method by (entries 0 and 1
are `Init` and `Main`). The decompiled names (`E6Async`, `W18`) are numbers until someone
names them; this repo's `data/names/` holds the names people gave, and the decompiler applies them
([source-repo.md](source-repo.md#names)).

## Several workers in a scene

A cutscene is usually one worker telling the others what to do, one step at a time:

```csharp
// in the worker that runs the scene
_ = Wakka.WalkToBoatAsync();                      // start Wakka's entry, don't wait
await Tidus.LookAtWakkaAsync();                   // run Tidus's entry and wait for it
Common.WaitFrames(30);                            // 30 frames
```

What `await` means here: wait until that worker has run the entry. The other worker runs it, not
this one; this worker only waits.

## Reading a decompiled script

- The script's folder (`Events/Ka_ThunderPlains/Kami0000/`) has the script class
  (`Kami0000.cs`, the list of workers in `[Workers(…)]` order), one file per worker, the layout
  (`Kami0000.Layout.cs`) and the data (`Kami0000.Data.cs`): bytes you do not edit.
- Attributes such as `[Header]`, `[Pin]`, `[Ints]`, `[Floats]` and `[Labels]` are there so that the
  source compiles back to exactly the game's bytes. Read past them.
- `if (false) { … }` is code the developers turned off; it is kept because it is in the file.
- `goto` appears where the original's jumps have no `if`/`while` shape.
- `Text[71] // "…"`: the comment is the English message, from your game.
- `Dialogue.Say(Text[71]);` is one line of dialogue: open the window, wait for the player, close it.
  It is an inline method ([language.md](language.md)): the compiler writes the game's original calls.
- A worker deriving from `Library.Shared0123` has its code in `Library/Shared0123.cs`, shared by
  every worker with the same code.

## Where to go next

- [tutorial.md](tutorial.md): build and install a first mod.
- [language.md](language.md): what C# Atel accepts, entry by entry.
- [editor.md](editor.md): see the message text in VS Code.
