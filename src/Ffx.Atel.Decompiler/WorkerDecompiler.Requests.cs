// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Ffx.Atel.Bytecode.Code;

namespace Ffx.Atel.Decompiler;

// Entry methods as the plan's three kinds of code (docs/plan.md, "Workers and entries", "Requests
// between workers"): the engine's entries 0 and 1 as Init/Main overrides, entries other workers
// request as `public static async Task E5Async()`, the rest as `[Entry(n)] void En()`; a method that
// awaits a request is `async`.
internal sealed partial class WorkerDecompiler {
    /// <summary>The head entries of the methods written <c>async Task</c>, because other workers request them.</summary>
    public HashSet<int> AsyncEntries { get; } = [];

    /// <summary>Of <see cref="AsyncEntries"/>, those some request runs at a level other than the default: they take a <see cref="Priority"/>.</summary>
    public HashSet<int> PriorityEntries { get; } = [];

    /// <summary>Methods written <c>async Task</c> (<see cref="AsyncEntries"/>) and other methods made <c>async</c> by an <c>await</c>.</summary>
    public int AsyncTaskMethods { get; private set; }

    public int AsyncVoidMethods { get; private set; }

    /// <summary>The methods requests resolve to (see <see cref="StatementPrinter.RequestTarget"/>).</summary>
    public Func<int, int, string?>? RequestTarget {
        get => _printer.RequestTarget;
        set => _printer.RequestTarget = value;
    }

    public bool IsFunctionWorker => _functionWorker;

    /// <summary>The C# method name for a method whose first entry is <paramref name="entry"/>, when it is <c>async Task</c>.</summary>
    public static string AsyncName(int entry) => string.Create(CultureInfo.InvariantCulture, $"E{entry}Async");

    /// <summary>Hand-written method names by first entry (<see cref="Naming.EntryNaming"/>), without <c>Async</c>.</summary>
    public Dictionary<int, string> EntryNames { get; } = [];

    /// <summary>The <c>async Task</c> method of a first entry: <c>E5Async</c>, or the overlay's name with <c>Async</c>.</summary>
    public string AsyncMethodName(int entry) => EntryNames.TryGetValue(entry, out string? name) ? name + "Async" : AsyncName(entry);

    /// <summary>A plain entry method: <c>E5</c>, or the overlay's name.</summary>
    private string PlainMethodName(int entry) => EntryNames.TryGetValue(entry, out string? name) ? name : string.Create(CultureInfo.InvariantCulture, $"E{entry}");

    /// <summary>The engine's entries 0 and 1 are the base class's Init and Main, where a method holds only that one.</summary>
    private string? EngineName(Method m) => _functionWorker ? null : m.Entries switch { [0] => "Init", [1] => "Main", _ => null };

    /// <summary>
    ///     The entries a request can name by method: the first entry of each method that is neither an
    ///     engine override nor in a function worker. Entries that share a method's offset but are not its
    ///     first, and entries a merged method starts with <c>Entry(n);</c>, have no method of their own.
    /// </summary>
    public IEnumerable<int> MethodEntries() => _functionWorker ? [] : _methods.Where(m => EngineName(m) is null).Select(m => m.Entries[0]);

    /// <summary>Every request the worker's code makes.</summary>
    public IEnumerable<RequestStmt> RequestsMade() {
        List<RequestStmt> found = [];
        // Inside the calls of inline methods too: a helper of the worker's own may request (its places' code).
        foreach (Method m in _methods) Visit(m.Body, s => {
            if (s is RequestStmt r) found.Add(r);
        }, inlineBodies: true);
        return found;
    }

    private bool Awaits(Method m) {
        bool awaits = false;
        Visit(m.Body, s => {
            if (s is RequestStmt r && _printer.FormOf(r, out _, out _) is RequestForm.AwaitEnd or RequestForm.AwaitStart) awaits = true;
            // A call of an async inline method of the worker's own is `await Repeated01();`.
            if (s is InlineStmt i && _printer.AsyncInlines.Contains(i.Macro)) awaits = true;
        });
        return awaits;
    }

    /// <summary>Every statement, nested ones and a <c>for</c>'s initializers and steps included.</summary>
    /// <param name="inlineBodies">Also the code of inline method calls (what they lower to).</param>
    private static void Visit(IReadOnlyList<Stmt> list, Action<Stmt> visit, bool inlineBodies = false) {
        foreach (Stmt s in list) {
            visit(s);
            switch (s) {
                case IfStmt i:
                    Visit(i.Then, visit, inlineBodies);
                    if (i.Else is not null) Visit(i.Else, visit, inlineBodies);
                    break;
                case WhileStmt w: Visit(w.Body, visit, inlineBodies); break;
                case ForStmt f:
                    Visit(f.Init, visit, inlineBodies);
                    Visit(f.Step, visit, inlineBodies);
                    Visit(f.Body, visit, inlineBodies);
                    break;
                case DoStmt d: Visit(d.Body, visit, inlineBodies); break;
                case SwitchStmt sw: foreach (SwitchSection x in sw.Sections) Visit(x.Body, visit, inlineBodies); break;
                case InlineStmt x when inlineBodies: Visit(x.Body, visit, inlineBodies); break;
            }
        }
    }

    private void WriteHeader(StringBuilder sb, Method method) {
        if (IsRunFunction) {
            sb.Append(Indent).Append("public static void Run()\n");
            return;
        }
        string async = Awaits(method) ? "async " : "";
        if (EngineName(method) is string engine) {
            Overrides++;
            if (async.Length > 0) AsyncVoidMethods++;
            sb.Append(Indent).Append(CultureInfo.InvariantCulture, $"protected override {async}void {engine}()\n");
            return;
        }
        sb.Append(Indent).Append(string.Join("", method.Entries.Select(n => string.Create(CultureInfo.InvariantCulture, $"[Entry({n})]")))).Append('\n');
        int head = method.Entries[0];
        if (AsyncEntries.Contains(head)) {
            AsyncTaskMethods++;
            sb.Append(Indent).Append(CultureInfo.InvariantCulture, $"public static async Task {AsyncMethodName(head)}({(PriorityEntries.Contains(head) ? "Priority priority = Priority.P1" : "")})\n");
            return;
        }
        if (async.Length > 0) AsyncVoidMethods++;
        sb.Append(Indent).Append(CultureInfo.InvariantCulture, $"{async}void {PlainMethodName(head)}()\n");
    }
}
