// SPDX-License-Identifier: MIT

namespace Ffx.Atel.Bytecode.Script;

/// <summary>
///     What each opcode takes from and leaves on the worker's value stack, from the handlers in
///     <c>Script_Run</c> (<c>0x008641e0</c>) as ffx-rust ports them (<c>vm/bridge/run.rs</c>,
///     <c>signal.rs</c>).
/// </summary>
/// <remarks>
///     A native call (<c>func</c>) pops its arguments and always pushes one result:
///     <c>Script_FinishNative</c> (<c>0x008777f0</c>) pushes the float slot's result if there is one,
///     else the int slot's, else 0. <c>func_facc</c> then pops that result into the float accumulator.
///     Every request (<c>req*</c>, <c>reqw*</c>, <c>reqe*</c>, <c>39</c>–<c>3b</c>) pops entry, worker and
///     level (pushed in the order level, worker, entry) and pushes whether it was queued.
/// </remarks>
public static class StackEffects {
    /// <summary>
    ///     (pops, pushes) of an opcode; for <c>func</c> and <c>func_facc</c> the pops exclude the native's
    ///     arguments (see <see cref="Native"/>).
    /// </summary>
    public static (int Pops, int Pushes) Of(byte code) => code switch {
        Opcodes.Nop or 0x1d or 0x1e or Opcodes.Nop76 or Opcodes.Op40 => (0, 0),
        >= Opcodes.Lor and <= Opcodes.Mod => (2, 1),
        Opcodes.Not or Opcodes.Neg or Opcodes.Bnot => (1, 1),
        Opcodes.PushV => (0, 1),
        Opcodes.PopV or Opcodes.PopV1 => (1, 0),
        Opcodes.PushVA => (1, 1),
        Opcodes.PopVA or Opcodes.PopVA1 => (2, 0),
        Opcodes.PopFacc => (1, 0),
        Opcodes.PushFacc => (0, 1),
        Opcodes.PushVRef => (1, 1),
        Opcodes.PushA or Opcodes.PushB => (0, 1),
        Opcodes.PopA or Opcodes.PopB => (1, 0),
        Opcodes.Op2b => (1, 1),
        Opcodes.PushC or Opcodes.PushImm or Opcodes.PushFC => (0, 1),
        Opcodes.Jmp or Opcodes.Jnz or Opcodes.Jz or Opcodes.Call or Opcodes.Ret => (0, 0),
        Opcodes.Func => (0, 1),
        Opcodes.FuncFacc => (0, 0),
        >= Opcodes.Req0 and <= Opcodes.Op3b => (3, 1),
        >= Opcodes.Req1 and <= 0x53 => (3, 1),
        Opcodes.End or Opcodes.End1 or Opcodes.Op54 => (0, 0),
        Opcodes.EndP or Opcodes.EndP1 => (1, 0),
        Opcodes.PopAJmp or Opcodes.PopAJnz or Opcodes.PopAJz => (1, 0),
        >= Opcodes.PopI0 and <= 0x66 => (1, 0),
        >= Opcodes.PushI0 and <= Opcodes.PushG => (0, 1),
        Opcodes.Op77 or Opcodes.Op78 => (2, 0),
        Opcodes.Op79 => (3, 0),
        Opcodes.Op7a => (2, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(code), $"opcode 0x{code:x2} is invalid"),
    };

    /// <summary>Whether execution can continue with the next instruction.</summary>
    public static bool FallsThrough(byte code) => code is not (Opcodes.Jmp or Opcodes.PopAJmp or Opcodes.Ret
        or Opcodes.End or Opcodes.EndP or Opcodes.End1 or Opcodes.EndP1 or Opcodes.Op54);

    /// <summary>Whether the instruction jumps to a label (its operand).</summary>
    public static bool Jumps(byte code) => code is Opcodes.Jmp or Opcodes.Jnz or Opcodes.Jz
        or Opcodes.PopAJmp or Opcodes.PopAJnz or Opcodes.PopAJz;

    /// <summary>The stack effect of a native call with <paramref name="argc"/> arguments.</summary>
    public static (int Pops, int Pushes) Native(byte code, int argc) => code == Opcodes.FuncFacc ? (argc, 0) : (argc, 1);
}
