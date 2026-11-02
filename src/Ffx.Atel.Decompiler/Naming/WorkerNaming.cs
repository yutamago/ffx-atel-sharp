// SPDX-License-Identifier: MIT

using System.Reflection;
using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Decompiler.Naming;

/// <summary>
///     Names for workers where the bytes say what they are: a worker that loads exactly one model
///     (<c>Common.SetModel</c>, native <c>0x0001</c>, with a constant) is named after that model's member of
///     <see cref="Ffx.Model"/> (the PS2 headers' <c>chr_</c> names): <c>Tidu</c>, <c>Takarabox002</c>. Several
///     workers with the same model get their number (<c>Tidu_03</c>, <c>Tidu_07</c>); a model without a
///     <c>chr_</c> name (only its file code, <c>W101</c>) names nothing; all others keep
///     <c>W03</c>. Function workers keep theirs too.
/// </summary>
internal static class WorkerNaming {
    private const ushort SetModel = 0x0001;

    /// <summary>The names, given the default ones (<c>W00</c>, …) and names already taken in the source.</summary>
    public static string[] Names(ScriptBlock block, ScriptImage image, string[] defaults, IEnumerable<string> taken) {
        HashSet<string> reserved = new(taken, StringComparer.Ordinal);
        reserved.UnionWith(Reserved.Value);
        Domain models = GameNames.Enum(typeof(Ffx.Model))!;
        string?[] baseNames = new string?[defaults.Length];
        for (int w = 0; w < defaults.Length; w++) {
            Bytecode.Script.Worker worker = block.Workers[w];
            if (worker.Type == 0) continue;
            HashSet<int> loaded = [];
            bool unknown = false;
            List<Instr> code = block.Decode(worker);
            for (int i = 0; i < code.Count; i++) {
                if (code[i].Code is not (Opcodes.Func or Opcodes.FuncFacc) || code[i].Operand != SetModel) continue;
                Instr arg = i > 0 ? code[i - 1] : default;
                int? value = arg.Code switch {
                    Opcodes.PushImm => (short)arg.Operand!.Value,
                    Opcodes.PushC when arg.Operand < image.IntConstants.Count => (int)image.IntConstants[arg.Operand!.Value],
                    _ => null,
                };
                if (value is int v) loaded.Add(v);
                else unknown = true;
            }
            if (unknown || loaded.Count != 1) continue;
            string? member = models.Member(loaded.First());
            string? name = member?[(member.IndexOf('.', StringComparison.Ordinal) + 1)..];
            // Only the headers' chr_ names: a file code (W101, C001) reads like a default name.
            if (name is not null && !(name.Length > 1 && char.IsAsciiLetterUpper(name[0]) && name[1..].All(char.IsAsciiDigit))) baseNames[w] = name;
        }
        string[] names = (string[])defaults.Clone();
        foreach (IGrouping<string, int> g in Enumerable.Range(0, defaults.Length).Where(w => baseNames[w] is not null).GroupBy(w => baseNames[w]!)) {
            foreach (int w in g) {
                string name = g.Count() == 1 ? g.Key : $"{g.Key}_{defaults[w][1..]}";
                if (!reserved.Contains(name)) names[w] = name;
            }
        }
        return names;
    }

    /// <summary>Names a worker class must not take: the reference assembly's types and the members of <c>Worker</c>.</summary>
    private static readonly Lazy<HashSet<string>> Reserved = new(() => {
        System.Reflection.Assembly game = typeof(NativeAttribute).Assembly;
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (Type t in game.GetExportedTypes()) names.Add(t.Name.Split('`')[0]);
        foreach (MemberInfo m in typeof(Ffx.Atel.Worker).GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) names.Add(m.Name);
        return names;
    });
}
