// SPDX-License-Identifier: MIT
#pragma warning disable ATL3007 // the bad example: SnippetTests checks that it raises ATL3007
// SPDX-License-Identifier: MIT
using Ffx;
using Ffx.Atel;

namespace Library
{
    public abstract class Greet : FunctionWorker
    {
        public static void Run()
        {
            return;
        }
    }
}

namespace Snippets.Atl3007
{
    [Event("test3007")]
    [Workers(typeof(GreetA), typeof(GreetB), typeof(Director))]
    public static partial class Test3007
    {
    }

    // region bad
    public sealed partial class GreetA : Library.Greet { }
    public sealed partial class GreetB : Library.Greet { }

    public sealed partial class Director : LogicWorker
    {
        [Entry(2)]
        void Start()
        {
            Library.Greet.Run();        // ATL3007: GreetA and GreetB both derive from it
        }
    }
    // endregion

    // region fix
    public sealed partial class Director
    {
        [Entry(3)]
        void StartGreetA()
        {
            GreetA.Run();               // name the worker
        }
    }
    // endregion
}
