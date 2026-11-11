// SPDX-License-Identifier: MIT
#pragma warning disable ATL1002, CS0219 // the bad example: SnippetTests checks that it raises ATL1002
using Ffx;
using Ffx.Atel;
using static Snippets.Atl1002.Test1002;

namespace Snippets.Atl1002;

[Event("test1002")]
[Workers(typeof(Guard))]
public static partial class Test1002
{

}

public sealed partial class Guard : Actor
{
    // region bad
    [Entry(2)]
    void Greet()
    {
        string name = "Wakka";          // ATL1002
    }
    // endregion

    // region fix
    [Entry(3)]
    void GreetWithAMessage()
    {
        Dialogue.Say(Text[12]);         // a line of the script's message table
        Debug.Print("greeted Wakka");   // text for the log is fine
    }
    // endregion
}
