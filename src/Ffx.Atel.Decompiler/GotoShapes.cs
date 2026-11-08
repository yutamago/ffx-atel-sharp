// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Code;

namespace Ffx.Atel.Decompiler;

/// <summary>
///     Sorts the <c>goto</c>s a structured method still holds by shape (docs/plan.md, "Phase 4: control
///     flow", "The gotos that stay"): where the label lies against the jump, and which constructs the jump
///     leaves. Counting only; nothing here changes the code.
/// </summary>
internal static class GotoShapes {
    private sealed record Place(IReadOnlyList<Stmt> Block, int Index);

    /// <summary>Adds the shape of every <c>goto</c> in <paramref name="body"/> to <paramref name="counts"/>.</summary>
    public static void Count(IReadOnlyList<Stmt> body, Dictionary<string, int> counts) {
        Dictionary<int, List<Place>> labels = [];
        Dictionary<int, int> jumps = [];
        List<(int Label, bool Conditional, List<Place> Path)> gotos = [];
        void Walk(IReadOnlyList<Stmt> list, List<Place> path) {
            for (int i = 0; i < list.Count; i++) {
                List<Place> here = [.. path, new Place(list, i)];
                switch (list[i]) {
                    case LabelStmt l: labels[l.Label] = here; break;
                    case GotoStmt g: gotos.Add((g.Label, false, here)); jumps[g.Label] = jumps.GetValueOrDefault(g.Label) + 1; break;
                    case CondGotoStmt g: gotos.Add((g.Label, true, here)); jumps[g.Label] = jumps.GetValueOrDefault(g.Label) + 1; break;
                    case IfStmt s:
                        Walk(s.Then, here);
                        if (s.Else is not null) Walk(s.Else, here);
                        break;
                    case WhileStmt w: Walk(w.Body, here); break;
                    case ForStmt f: Walk(f.Body, here); break;
                    case DoStmt d: Walk(d.Body, here); break;
                    case SwitchStmt sw: foreach (SwitchSection x in sw.Sections) Walk(x.Body, here); break;
                }
            }
        }
        Walk(body, []);
        foreach ((int label, bool conditional, List<Place> path) in gotos) {
            string shape = Shape(label, path, labels);
            string key = shape + (conditional ? " (if)" : "");
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (jumps[label] > 1) counts["shared-label"] = counts.GetValueOrDefault("shared-label") + 1;
            // `goto lN;` followed by a statement only the original's dead code reaches.
            if (!conditional && path[^1] is { } at && at.Index + 1 < at.Block.Count && at.Block[at.Index + 1] is not (LabelStmt or EntryStmt)) {
                counts["dead-after"] = counts.GetValueOrDefault("dead-after") + 1;
            }
        }
    }

    private static string Shape(int label, List<Place> path, Dictionary<int, List<Place>> labels) {
        if (!labels.TryGetValue(label, out List<Place>? at)) return "no-label";
        Place target = at[^1];
        int k = path.FindIndex(p => ReferenceEquals(p.Block, target.Block));
        if (k < 0) return "into-block";
        int from = path[k].Index;
        bool nested = k < path.Count - 1;
        // The constructs the jump leaves, innermost last.
        List<Stmt> crossed = [.. path.Skip(k).Take(path.Count - 1 - k).Select(p => p.Block[p.Index])];
        bool loops = crossed.Any(s => s is WhileStmt or ForStmt or DoStmt);
        bool switches = crossed.Any(s => s is SwitchStmt);
        if (target.Index <= from) {
            if (!nested) return "back";
            return loops ? "back-out-of-loop" : switches ? "back-out-of-switch" : "back-out-of-if";
        }
        bool adjacent = Enumerable.Range(from + 1, target.Index - from - 1).All(i => target.Block[i] is LabelStmt or EntryStmt);
        if (!nested) return adjacent ? "next" : "forward";
        if (adjacent) return loops ? "break-out-of-loop" : switches ? "break-out-of-switch" : "end-of-if";
        return loops ? "forward-out-of-loop" : switches ? "forward-out-of-switch" : "forward-out-of-if";
    }
}
