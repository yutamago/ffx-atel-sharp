using Ffx;
using Ffx.Atel;
using static AtelScriptNamespace.AtelScriptClass;

// The namespace names the script this file changes: AtelScriptNamespace is the vanilla source in
// <vanilla>/AtelScriptFolder/. A mod adds to the script's classes with partial classes of the same name.
namespace AtelScriptNamespace;

// The script class (atel-script-name's variables). A field here is a new variable of the mod; the loader
// places it when it links the mod onto the player's file.
public static partial class AtelScriptClass
{
    public static int AtelModIdentifierCounter;
}

// A vanilla worker gets code of the mod the same way: a partial class with the worker's name, and a
// method that runs before, after, instead of or inside one of the worker's methods. For example, in the
// Larva (m209), whose action worker is Ai:
//
// public sealed partial class Ai
// {
//     [Postfix(nameof(Init))]
//     void MoreHp()
//     {
//         Btl.SetActorProperty(ActorProperty.Hp, 1234);
//     }
// }
//
// Open the vanilla source of atel-script-name in the IDE to see its workers and methods.
