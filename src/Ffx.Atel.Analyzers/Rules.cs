// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Analyzers;

/// <summary>
///     Every ATL diagnostic, documented in <c>docs/analyzers.md</c>. Numbering: ATL0xxx the plan's own
///     warnings (registers), ATL1xxx C# that ATEL cannot run (the subset), ATL2xxx requests between
///     workers, ATL3xxx <c>call</c> (functions and helpers).
/// </summary>
public static class Rules {
    private const string Docs = "https://github.com/yutamago/ffx-atel-sharp/blob/main/docs/analyzers.md#";

    public const string SubsetCategory = "Atel.Subset";
    public const string RequestCategory = "Atel.Requests";
    public const string CallCategory = "Atel.Calls";
    public const string RegisterCategory = "Atel.Registers";

    private static DiagnosticDescriptor Rule(string id, string title, string message, string category, DiagnosticSeverity severity, string description, params string[] tags) =>
        new(id, title, message, category, severity, isEnabledByDefault: true, description, Docs + id.ToLowerInvariant(), tags);

    // ----- ATL0xxx: registers ---------------------------------------------------------------

    public static readonly DiagnosticDescriptor RegisterAcrossWait = Rule(
        "ATL0001", "Register field used across a wait while another entry writes it",
        "Register field '{0}' holds a value across this wait, and entry '{1}' of the same worker writes it",
        RegisterCategory, DiagnosticSeverity.Warning,
        "A worker has one set of registers for all its entries. While an entry waits, another entry of the same worker can run and overwrite the register.");

    // ----- ATL1xxx: the subset ----------------------------------------------------------------

    public static readonly DiagnosticDescriptor ObjectCreation = Rule(
        "ATL1001", "Objects are not created at run time",
        "{0} is not available in Atel: ATEL has no objects, so nothing is created with 'new' at run time",
        SubsetCategory, DiagnosticSeverity.Error,
        "Workers are created by the game, not by code. Arrays are fields with a fixed size and their initial values as field initializers.");

    public static readonly DiagnosticDescriptor String = Rule(
        "ATL1002", "String outside Debug",
        "'string' is not available here: ATEL has no strings; messages are IDs ('Text[12]') and debug text goes only to 'Debug.*'",
        SubsetCategory, DiagnosticSeverity.Error,
        "Strings are allowed as attribute arguments and as arguments to reference-assembly members that take one (Debug.*, Asm, Text.New).");

    public static readonly DiagnosticDescriptor BaseLibrary = Rule(
        "ATL1003", ".NET base library",
        "'{0}' is from the .NET base library, which does not exist in the game's script interpreter",
        SubsetCategory, DiagnosticSeverity.Error,
        "Atel code compiles to ATEL bytecode; only the reference assembly (Ffx.Atel.Game) and the project's own code are available.");

    public static readonly DiagnosticDescriptor Collection = Rule(
        "ATL1004", "Collections",
        "Collection '{0}' is not available in Atel; use a fixed-size array field",
        SubsetCategory, DiagnosticSeverity.Error,
        "ATEL has variables and fixed-size arrays in the script's storage areas, nothing that grows.");

    public static readonly DiagnosticDescriptor Linq = Rule(
        "ATL1005", "LINQ",
        "LINQ ('{0}') is not available in Atel; write the loop",
        SubsetCategory, DiagnosticSeverity.Error,
        "LINQ needs delegates, enumerators and the .NET base library, none of which ATEL has.");

    public static readonly DiagnosticDescriptor Exceptions = Rule(
        "ATL1006", "Exceptions",
        "Exceptions ('{0}') are not available in Atel",
        SubsetCategory, DiagnosticSeverity.Error,
        "ATEL has no exceptions: no 'try', 'catch', 'finally' or 'throw'.");

    public static readonly DiagnosticDescriptor Delegates = Rule(
        "ATL1007", "Lambdas and delegates",
        "{0} is not available in Atel: ATEL has no function values",
        SubsetCategory, DiagnosticSeverity.Error,
        "ATEL calls only fixed targets: natives, functions and helpers by 'call', entries by request. Lambdas, anonymous methods, local functions, delegate types and method groups as values have no ATEL form.");

    public static readonly DiagnosticDescriptor Generics = Rule(
        "ATL1008", "Generic declarations",
        "Generic {0} '{1}' is not available in Atel; only the reference assembly's generics are",
        SubsetCategory, DiagnosticSeverity.Error,
        "Code compiles to one ATEL body per method, so a method or type cannot be generic over types.");

    public static readonly DiagnosticDescriptor Type = Rule(
        "ATL1009", "Not an ATEL type",
        "'{0}' is not an ATEL type; ATEL has int, uint, short, ushort, sbyte, byte, float, bool, enums and fixed-size arrays",
        SubsetCategory, DiagnosticSeverity.Error,
        "ATEL values are 32 bits wide: 'long', 'ulong', 'double', 'decimal', 'char', 'object' and native-sized integers have no representation. Float literals need their 'f'.");

    public static readonly DiagnosticDescriptor Checked = Rule(
        "ATL1010", "checked arithmetic",
        "'checked' is not available in Atel: ATEL integers wrap",
        SubsetCategory, DiagnosticSeverity.Error,
        "ATEL has no overflow check; 'int' arithmetic wraps as in C#'s default unchecked context.");

    // ATL1011 (`&&` and `||`, "not yet") is retired: the compiler lowers them to branches.

    // ----- ATL2xxx: requests between workers ----------------------------------------------------

    public static readonly DiagnosticDescriptor RequestNotAwaited = Rule(
        "ATL2001", "Request neither awaited nor discarded",
        "The request to '{0}' is queued and the caller carries on; write 'await' to wait for it or '_ =' to make that explicit",
        RequestCategory, DiagnosticSeverity.Info,
        "As C#'s CS4014: 'await W.NameAsync()' waits until the entry ends (reqe), '_ = W.NameAsync()' queues it (req).");

    public static readonly DiagnosticDescriptor RequestToNonAsync = Rule(
        "ATL2002", "Request to an entry that is not async",
        "'{0}' is an entry, so it runs on its worker through a request; requestable entries are declared 'async Task {0}Async()'",
        RequestCategory, DiagnosticSeverity.Error,
        "Entries are not functions: they run on their own worker when it gets to them. Only 'async' entries can be requested from code.");

    public static readonly DiagnosticDescriptor AwaitNothing = Rule(
        "ATL2003", "await on something that is not a request",
        "'{0}' is not a request, so it completes at once; the 'await' is dropped",
        RequestCategory, DiagnosticSeverity.Info,
        "Only requests (and their .Finished and .Started) wait for another worker. Natives that wait do so anyway, and functions and helpers run synchronously.");

    public static readonly DiagnosticDescriptor SelfRequestDeadlock = Rule(
        "ATL2004", "Awaited request to the worker's own entry at the same or a lower priority",
        "Awaited request to entry {0} of this worker at level {1} from code that runs at level {2} ({3}): it cannot start while this entry waits",
        RequestCategory, DiagnosticSeverity.Error,
        "A worker runs one level at a time. An awaited request to its own entry starts only if its level is above the caller's; at the same or a lower level the caller waits for ever. A helper method is usually the better choice.");

    public static readonly DiagnosticDescriptor SelfRequest = Rule(
        "ATL2005", "Awaited request to the worker's own entry",
        "Awaiting '{0}', an entry of this worker, waits for ever unless the request's priority is above the caller's; consider a helper method",
        RequestCategory, DiagnosticSeverity.Warning,
        "A worker runs one level at a time. An awaited request to its own entry starts only if its level is above the caller's.");

    public static readonly DiagnosticDescriptor Priority = Rule(
        "ATL2006", "Priority reserved for the engine",
        "Priority {0} is {1}; scripts request at levels 1-6",
        RequestCategory, DiagnosticSeverity.Warning,
        "Level 0 is Main's, level 7 Init's and the battle system's, level 8 is unused. A request at one of them competes with the engine's own entries.");

    public static readonly DiagnosticDescriptor AsyncReturn = Rule(
        "ATL2007", "async method without Ffx.Atel.Task",
        "async method '{0}' must return Ffx.Atel.Task or Ffx.Atel.Task<T>, the reference assembly's request types (only an entry the engine runs may be async void)",
        RequestCategory, DiagnosticSeverity.Error,
        "An async method is a requestable entry; its return type is the request.");

    public static readonly DiagnosticDescriptor EntryFieldsOverlap = Rule(
        "ATL2008", "Entry with parameters or a result requested where requests may overlap",
        "'{0}' takes its parameters and gives its result through hidden fields of its worker; {1}, so one request can overwrite them before another has run",
        RequestCategory, DiagnosticSeverity.Warning,
        "An entry's parameters are written when it is requested and read when it runs; its result is written when it ends and read after .Finished. That is safe while one request at a time is pending: requests from several methods, or ones that do not wait for the entry to finish, may overlap.");

    // ----- ATL3xxx: call ------------------------------------------------------------------------

    public static readonly DiagnosticDescriptor Recursion = Rule(
        "ATL3001", "Recursion",
        "Recursion: {0}; ATEL's 'call' shares the caller's registers and has no stack frames",
        CallCategory, DiagnosticSeverity.Error,
        "Functions and helpers run with 'call', which keeps only a return address (at most 3) and shares the caller's registers. A function cannot call itself, directly or through others.",
        WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor CallDepth = Rule(
        "ATL3002", "call deeper than 3",
        "This call goes {0} deep ({1}); ATEL allows 3",
        CallCategory, DiagnosticSeverity.Error,
        "A worker level keeps at most 3 return addresses for 'call'. Each function or helper call is one 'call'.",
        WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor InlineRecursion = Rule(
        "ATL3003", "Inline method calls itself",
        "Inline method {0}; its code is written out in place of each call, so it cannot call itself",
        CallCategory, DiagnosticSeverity.Error,
        "An [Inline] method is not called: the compiler writes its code where the call is. A method that calls itself, directly or through other inline methods, would be written out without end.",
        WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor InlineArgument = Rule(
        "ATL3004", "Argument of an inline method with an effect",
        "This argument of inline method '{0}' is substituted where its parameter is used and evaluated there, perhaps several times or none; pass a constant, a variable or an expression of them",
        CallCategory, DiagnosticSeverity.Error,
        "An [Inline] method's arguments are substituted like a C macro's: each is evaluated where its parameter is used. An argument that calls something, assigns, or increments would run as often as the parameter is used. Compute the value into a variable first.");
}
