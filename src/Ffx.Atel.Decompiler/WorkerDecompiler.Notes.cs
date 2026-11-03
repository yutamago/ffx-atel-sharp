// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;
using Ffx.Atel.Decompiler.Naming;

namespace Ffx.Atel.Decompiler;

// What FlowNotes needs to know of a worker's methods: their names, which entries each holds, the requests
// and calls each makes, and the story switches of Init and Main.
internal sealed partial class WorkerDecompiler {
    /// <summary>Where each method starts in the text <see cref="WriteMethods"/> wrote (from the builder's start), in method order.</summary>
    public List<int> MethodOffsets { get; } = [];

    public int MethodCount => _methods.Count;

    /// <summary>The C# name of method <paramref name="m"/>: <c>Init</c>, <c>Main</c>, <c>Run</c>, <c>E5Async</c>, <c>E5</c> or the overlay's name.</summary>
    public string MethodName(int m) {
        Method method = _methods[m];
        if (IsRunFunction) return "Run";
        if (EngineName(method) is string engine) return engine;
        int head = method.Entries[0];
        return AsyncEntries.Contains(head) ? AsyncMethodName(head) : PlainMethodName(head);
    }

    /// <summary>The entries method <paramref name="m"/> holds: those it starts with, then those that start inside it (<c>Entry(n);</c>).</summary>
    public List<int> EntriesOf(int m) {
        Method method = _methods[m];
        List<int> entries = [.. method.Entries];
        Visit(method.Body, s => {
            if (s is EntryStmt e && !entries.Contains(e.Entry)) entries.Add(e.Entry);
        });
        return entries;
    }

    /// <summary>
    ///     The requests (<c>req</c>, <c>reqw</c>, <c>reqe</c>) of a constant worker and entry, and the
    ///     <c>call</c>s of function workers (entry 0), each with the method that makes it, in code order.
    /// </summary>
    public IEnumerable<(int Method, int Worker, int Entry, bool Call)> Calls() {
        List<(int, int, int, bool)> found = [];
        for (int m = 0; m < _methods.Count; m++) {
            int method = m;
            Visit(_methods[m].Body, s => {
                switch (s) {
                    case RequestStmt { Worker: IntLit { Value: int w }, Entry: IntLit { Value: int e } }:
                        found.Add((method, w, e, false));
                        break;
                    case CallWorkerStmt c:
                        found.Add((method, c.Worker, 0, true));
                        break;
                }
            });
        }
        return found;
    }

    /// <summary>The <c>switch (StoryProgress)</c> statements of the worker's <c>Init</c> and <c>Main</c>, outermost first, with their method.</summary>
    public IEnumerable<(int Method, SwitchStmt Switch)> StorySwitches() {
        List<(int, SwitchStmt)> found = [];
        for (int m = 0; m < _methods.Count; m++) {
            if (EngineName(_methods[m]) is null) continue;
            Find(_methods[m].Body, m);
        }
        return found;

        // Not into a story switch: its requests are its cases'.
        void Find(IReadOnlyList<Stmt> list, int m) {
            foreach (Stmt s in list) {
                switch (s) {
                    case SwitchStmt sw when _printer.SelectorDomain(sw.Selector) == GameNames.Story: found.Add((m, sw)); break;
                    case SwitchStmt sw: foreach (SwitchSection x in sw.Sections) Find(x.Body, m); break;
                    case IfStmt i:
                        Find(i.Then, m);
                        if (i.Else is not null) Find(i.Else, m);
                        break;
                    case WhileStmt w: Find(w.Body, m); break;
                    case ForStmt f: Find(f.Body, m); break;
                    case DoStmt d: Find(d.Body, m); break;
                }
            }
        }
    }

    /// <summary>The constant requests a block makes, nested ones included: (worker, entry), in code order.</summary>
    public static List<(int Worker, int Entry)> RequestsIn(IReadOnlyList<Stmt> body) {
        List<(int, int)> found = [];
        Visit(body, s => {
            if (s is RequestStmt { Worker: IntLit { Value: int w }, Entry: IntLit { Value: int e } }) found.Add((w, e));
        });
        return found;
    }
}
