// SPDX-License-Identifier: MIT

using System.Reflection;
using Ffx.Atel.Bytecode.Analysis;

namespace Ffx.Atel.Compiler;

/// <summary>How a member reaches its native: called, read or written.</summary>
public enum MemberUse { Call, Get, Set }

/// <summary>What the native's first argument is, when the member does not pass it itself.</summary>
public enum MemberReceiver {
    /// <summary>Every argument is the member's.</summary>
    None,

    /// <summary>The worker it acts on: this one (<c>Common.GetWorkerId(-1)</c>), <c>Tidus.…</c> or <c>Workers[n].…</c>.</summary>
    Worker,

    /// <summary>The index of an indexed group: <c>Sprites[3].…</c>.</summary>
    Index,
}

/// <summary>
///     One native of the object-oriented surface (<see cref="NativeMemberAttribute"/>): the member's path, how it is
///     used, and how its arguments map onto the native's.
/// </summary>
/// <param name="Path">
///     The member as written without a receiver: <c>Pos</c>, <c>Turn.Start</c>, <c>Pos.X</c> (a component of a
///     <c>Vector3</c>), <c>Sprites.Show</c> (an indexed group's item), <c>Wait.Frames</c> (a static group).
/// </param>
/// <param name="Vector">A setter whose value is a <c>Vector3</c>: three arguments.</param>
/// <param name="Bool">A setter whose value is a <c>bool</c>.</param>
/// <param name="SelfTwin">
///     A worker native that has a native of its own for this worker under the same member and use
///     (<c>Yaw</c>: <c>GetYaw</c> and <c>GetWorkerYaw</c>): written without a worker the member is the other one, so
///     this one with <c>Common.GetWorkerId(-1)</c> has no member form.
/// </param>
/// <param name="Void">A method that returns nothing: only a statement.</param>
public sealed record MemberNative(string Path, ushort Id, MemberUse Use, MemberReceiver Receiver, bool Vector, bool Bool, bool Polls, bool Void = false) {
    public bool SelfTwin { get; init; }

    /// <summary>The root of the path: the worker's member, the group or the static class.</summary>
    public string Root => Path.Split('.')[0];

    /// <summary>The last part of the path.</summary>
    public string Name => Path.Split('.')[^1];

    /// <summary>Whether the root is a static class (<c>Wait</c>) rather than a member of the worker.</summary>
    public bool Static { get; init; }
}

/// <summary>
///     The object-oriented surface of the reference assembly (<c>Members.g.cs</c>, docs/language.md, "Members"): every
///     member with <see cref="NativeMemberAttribute"/>, by path and by native. The compiler lowers a member onto its
///     native; the decompiler writes the member for the native's call.
/// </summary>
public sealed class NativeMembers {
    private readonly Dictionary<(string Path, MemberUse Use), List<MemberNative>> _byPath = [];
    private readonly Dictionary<ushort, MemberNative> _byId = [];

    /// <summary>Roots that are members of the worker (<c>Pos</c>, <c>Turn</c>, <c>Sprites</c>, <c>EnableTrigger</c>).</summary>
    public HashSet<string> WorkerRoots { get; } = new(StringComparer.Ordinal);

    /// <summary>Roots that are static classes (<c>Wait</c>, <c>Camera</c>).</summary>
    public HashSet<string> StaticRoots { get; } = new(StringComparer.Ordinal);

    /// <summary>Roots indexed by the native's first argument (<c>Sprites</c>).</summary>
    public HashSet<string> IndexedRoots { get; } = new(StringComparer.Ordinal);

    /// <summary>Every member's native.</summary>
    public IEnumerable<MemberNative> All => _byId.Values.OrderBy(m => m.Id);

    /// <summary>The members at a path, used so.</summary>
    public IReadOnlyList<MemberNative> Find(string path, MemberUse use) => _byPath.GetValueOrDefault((path, use)) ?? [];

    /// <summary>The member a native is, if any.</summary>
    public MemberNative? this[ushort id] => _byId.GetValueOrDefault(id);

    /// <summary>The members of <c>Ffx.Atel.Game</c>.</summary>
    public static NativeMembers Default => s_default.Value;

    private static readonly Lazy<NativeMembers> s_default = new(() => FromAssembly(typeof(Worker).Assembly));

    /// <summary>Reads the members of an assembly: <c>Worker</c>'s static members and what they hold, and the static classes.</summary>
    public static NativeMembers FromAssembly(Assembly assembly) {
        NativeMembers m = new();
        Type worker = assembly.GetType("Ffx.Atel.Worker") ?? throw new InvalidOperationException("no Ffx.Atel.Worker");
        const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (PropertyInfo p in worker.GetProperties(Static)) {
            Type t = p.PropertyType;
            if (t.Name == "WorkerList") continue;
            if (t.GetProperty("Item", Instance) is PropertyInfo indexer && HasMembers(indexer.PropertyType)) {
                m.IndexedRoots.Add(p.Name);
                m.WorkerRoots.Add(p.Name);
                m.AddType(indexer.PropertyType, Instance, p.Name, MemberReceiver.Index, isStatic: false);
            }
            else if (t.IsClass && HasMembers(t)) {
                m.WorkerRoots.Add(p.Name);
                m.AddType(t, Instance, p.Name, MemberReceiver.None, isStatic: false);
                m.AddProperty(p, p.Name, MemberReceiver.None, isStatic: false);
            }
            else if (m.AddProperty(p, p.Name, MemberReceiver.None, isStatic: false)) {
                m.WorkerRoots.Add(p.Name);
            }
        }
        foreach (MethodInfo method in worker.GetMethods(Static)) {
            if (!method.IsSpecialName && m.AddMethod(method, method.Name, MemberReceiver.None, isStatic: false)) m.WorkerRoots.Add(method.Name);
        }
        foreach (Type t in assembly.GetExportedTypes()) {
            if (!(t.IsAbstract && t.IsSealed) || t.Namespace != "Ffx" || !HasMembers(t, Static)) continue;
            m.StaticRoots.Add(t.Name);
            m.AddType(t, Static, t.Name, MemberReceiver.None, isStatic: true);
        }
        // A worker native with a native of its own for this worker under the same member and use.
        foreach (List<MemberNative> list in m._byPath.Values) {
            for (int i = 0; i < list.Count; i++) {
                if (list[i].Receiver == MemberReceiver.Worker && list.Any(o => o.Receiver == MemberReceiver.None)) {
                    list[i] = list[i] with { SelfTwin = true };
                    m._byId[list[i].Id] = list[i];
                }
            }
        }
        return m;
    }

    private static IEnumerable<Attribute> Natives(MemberInfo member) =>
        member.GetCustomAttributes(false).OfType<Attribute>().Where(a => a.GetType().FullName == "Ffx.Atel.NativeMemberAttribute");

    private static bool HasMembers(Type t, BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) =>
        t.GetMethods(flags).Any(x => Natives(x).Any());

    private void AddType(Type t, BindingFlags flags, string root, MemberReceiver receiver, bool isStatic) {
        foreach (PropertyInfo p in t.GetProperties(flags)) {
            if (p.GetIndexParameters().Length == 0) AddProperty(p, $"{root}.{p.Name}", receiver, isStatic);
        }
        foreach (MethodInfo method in t.GetMethods(flags)) {
            if (!method.IsSpecialName) AddMethod(method, $"{root}.{method.Name}", receiver, isStatic);
        }
    }

    private bool AddProperty(PropertyInfo p, string path, MemberReceiver receiver, bool isStatic) {
        bool any = false;
        bool vector = p.PropertyType.FullName == "Ffx.Vector3";
        foreach ((MethodInfo? accessor, MemberUse use) in new[] { (p.GetMethod, MemberUse.Get), (p.SetMethod, MemberUse.Set) }) {
            if (accessor is null) continue;
            foreach (Attribute a in Natives(accessor)) {
                string? component = (string?)a.GetType().GetProperty("Component")!.GetValue(a);
                Add(new MemberNative(component is null ? path : $"{path}.{component}", Id(a), use, Receiver(a, receiver),
                    vector && use == MemberUse.Set, p.PropertyType == typeof(bool) && use == MemberUse.Set, Polls(a)) { Static = isStatic });
                any = true;
            }
        }
        return any;
    }

    private bool AddMethod(MethodInfo method, string path, MemberReceiver receiver, bool isStatic) {
        bool any = false;
        foreach (Attribute a in Natives(method)) {
            Add(new MemberNative(path, Id(a), MemberUse.Call, Receiver(a, receiver), false, false, Polls(a), method.ReturnType == typeof(void)) { Static = isStatic });
            any = true;
        }
        return any;
    }

    private static ushort Id(Attribute a) => (ushort)(int)a.GetType().GetProperty("Id")!.GetValue(a)!;

    private static bool Polls(Attribute a) => (bool)a.GetType().GetProperty("Polls")!.GetValue(a)!;

    private static MemberReceiver Receiver(Attribute a, MemberReceiver container) =>
        (bool)a.GetType().GetProperty("Worker")!.GetValue(a)! ? MemberReceiver.Worker : container;

    private void Add(MemberNative n) {
        if (!_byPath.TryGetValue((n.Path, n.Use), out List<MemberNative>? list)) _byPath[(n.Path, n.Use)] = list = [];
        // One entry per native.
        if (list.Any(x => x.Id == n.Id)) return;
        list.Add(n);
        _byId.TryAdd(n.Id, n);
    }

    /// <summary>The native's argument count the member gives (without the receiver): <c>sig.Argc</c> less one with a receiver.</summary>
    public static int ValueCount(MemberNative m, NativeSignature sig) => sig.Argc - (m.Receiver == MemberReceiver.None ? 0 : 1);
}
