// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode.Script;

namespace Ffx.Atel.Compiler.Tests;

/// <summary>
///     Enough of the script interpreter (<c>Script_Run</c>, as ffx-rust's <c>vm/bridge/run.rs</c> models it)
///     to run new code in tests: typed stack slots (arithmetic on two integers is integer arithmetic, with
///     a float operand float arithmetic; <c>neg</c> works on floats), variables by table index, the
///     registers, jumps, <c>call</c>/<c>ret</c> across workers, natives through a callback. Requests are
///     recorded, not run; a polling native returns at once.
/// </summary>
internal sealed class MiniVm(ScriptImage image, Func<ushort, MiniVm.Value[], MiniVm.Value>? native = null) {
    /// <summary>A stack value: its bits, as an integer or a float.</summary>
    public readonly record struct Value(bool IsFloat, uint Bits) {
        public static Value Int(int v) => new(false, (uint)v);

        public static Value Float(float v) => new(true, BitConverter.SingleToUInt32Bits(v));

        public int AsInt => IsFloat ? (int)BitConverter.UInt32BitsToSingle(Bits) : (int)Bits;

        public float AsFloat => IsFloat ? BitConverter.UInt32BitsToSingle(Bits) : (int)Bits;

        public override string ToString() => IsFloat ? $"{AsFloat}f" : $"{AsInt}";
    }

    private readonly Dictionary<(int Var, int Index), Value> _vars = [];
    private readonly Value[] _ints = new Value[4], _floats = new Value[10];
    private Value _a, _b;

    /// <summary>Natives called, in order: id and arguments.</summary>
    public List<(ushort Id, Value[] Args)> Calls { get; } = [];

    /// <summary>Runs a requested entry at once, as if it ran to its end before the requester goes on.</summary>
    public bool RunRequests { get; init; }

    /// <summary>Requests made: opcode, level, worker, entry.</summary>
    public List<(byte Op, int Level, int Worker, int Entry)> Requests { get; } = [];

    public Value Var(int index, int element = 0) => _vars.GetValueOrDefault((index, element), Value.Int(0));

    public void SetVar(int index, Value v, int element = 0) => _vars[(index, element)] = Store(image.Variables[index].Type, v);

    public Value Register(bool isFloat, int n) => isFloat ? _floats[n] : _ints[n];

    private static Value Store(int type, Value v) => type == 6 ? Value.Float(v.AsFloat) : type switch {
        0 => Value.Int((byte)v.AsInt), 1 => Value.Int((sbyte)v.AsInt), 2 => Value.Int((ushort)v.AsInt), 3 => Value.Int((short)v.AsInt),
        _ => Value.Int(v.AsInt),
    };

    /// <summary>Runs entry <paramref name="entry"/> of worker <paramref name="worker"/> until it ends; returns the instructions run.</summary>
    public int Run(int worker, int entry, int limit = 100_000) {
        Stack<Value> stack = [];
        Stack<(int Worker, int Pc)> calls = [];
        int w = worker, pc = (int)image.Workers[worker].Entries[entry], steps = 0;
        Value Pop() => stack.Pop();
        void Push(Value v) => stack.Push(v);
        while (true) {
            if (++steps > limit) throw new InvalidOperationException("the code does not end");
            byte[] code = image.Workers[w].Code;
            byte b = code[pc];
            byte op = (byte)(b & 0x7f);
            int operand = (b & 0x80) != 0 ? code[pc + 1] | code[pc + 2] << 8 : 0;
            int at = pc;
            pc += (b & 0x80) != 0 ? 3 : 1;
            int Label(int n) => (int)image.Workers[w].Labels[n];
            switch (op) {
                case Opcodes.Nop or Opcodes.Nop76: break;
                case Opcodes.Lor or Opcodes.Land: {
                    int y = Pop().AsInt, x = Pop().AsInt;
                    Push(Value.Int(op == Opcodes.Lor ? (x != 0 || y != 0 ? 1 : 0) : (x != 0 && y != 0 ? 1 : 0)));
                    break;
                }
                case Opcodes.Or or Opcodes.Xor or Opcodes.And or Opcodes.Shl or Opcodes.Sar or Opcodes.Mod: {
                    int y = Pop().AsInt, x = Pop().AsInt;
                    Push(Value.Int(op switch {
                        Opcodes.Or => x | y, Opcodes.Xor => x ^ y, Opcodes.And => x & y, Opcodes.Shl => x << (y & 31),
                        Opcodes.Sar => x >> (y & 31), _ => x % y,
                    }));
                    break;
                }
                case >= Opcodes.Eq and <= Opcodes.Le: {
                    Value y = Pop(), x = Pop();
                    bool r = !x.IsFloat && !y.IsFloat
                        ? op switch {
                            Opcodes.Eq => x.AsInt == y.AsInt, Opcodes.Ne => x.AsInt != y.AsInt, Opcodes.Gt => x.AsInt > y.AsInt,
                            Opcodes.Lt => x.AsInt < y.AsInt, Opcodes.Ge => x.AsInt >= y.AsInt, Opcodes.Le => x.AsInt <= y.AsInt,
                            Opcodes.Gtu => x.Bits > y.Bits, Opcodes.Ltu => x.Bits < y.Bits, Opcodes.Geu => x.Bits >= y.Bits, _ => x.Bits <= y.Bits,
                        }
                        : op switch {
                            Opcodes.Eq => x.AsFloat == y.AsFloat, Opcodes.Ne => x.AsFloat != y.AsFloat,
                            Opcodes.Gt or Opcodes.Gtu => x.AsFloat > y.AsFloat, Opcodes.Ge or Opcodes.Geu => x.AsFloat >= y.AsFloat,
                            Opcodes.Lt or Opcodes.Ltu => x.AsFloat < y.AsFloat, _ => x.AsFloat <= y.AsFloat,
                        };
                    Push(Value.Int(r ? 1 : 0));
                    break;
                }
                case >= Opcodes.Add and <= Opcodes.Div: {
                    Value y = Pop(), x = Pop();
                    if (!x.IsFloat && !y.IsFloat) {
                        int a = x.AsInt, c = y.AsInt;
                        Push(Value.Int(op switch { Opcodes.Add => unchecked(a + c), Opcodes.Sub => unchecked(a - c), Opcodes.Mul => unchecked(a * c), _ => a / c }));
                    }
                    else {
                        float a = x.AsFloat, c = y.AsFloat;
                        Push(Value.Float(op switch { Opcodes.Add => a + c, Opcodes.Sub => a - c, Opcodes.Mul => a * c, _ => a / c }));
                    }
                    break;
                }
                case Opcodes.Not: Push(Value.Int(Pop().AsInt == 0 ? 1 : 0)); break;
                case Opcodes.Neg: Push(Value.Float(-Pop().AsFloat)); break;
                case Opcodes.Bnot: Push(Value.Int(~Pop().AsInt)); break;
                case Opcodes.PushV: Push(Var(operand)); break;
                case Opcodes.PopV or Opcodes.PopV1: SetVar(operand, Pop()); break;
                case Opcodes.PushVA: Push(Var(operand, Pop().AsInt)); break;
                case Opcodes.PopVA or Opcodes.PopVA1: {
                    Value v = Pop();
                    SetVar(operand, v, Pop().AsInt);
                    break;
                }
                case Opcodes.PopFacc: Pop(); break;
                case Opcodes.PushA: Push(_a); break;
                case Opcodes.PopA: _a = Pop(); break;
                case Opcodes.PushB: Push(_b); break;
                case Opcodes.PopB: _b = Pop(); break;
                case Opcodes.Op2b: Push(stack.Peek()); break;
                case Opcodes.PushImm: Push(Value.Int((short)operand)); break;
                case Opcodes.PushC: Push(Value.Int((int)image.IntConstants[operand])); break;
                case Opcodes.PushFC: Push(new Value(true, image.FloatConstants[operand])); break;
                case Opcodes.Jmp: pc = Label(operand); break;
                case Opcodes.PopAJz: if (Pop().AsInt == 0) pc = Label(operand); break;
                case Opcodes.PopAJnz: if (Pop().AsInt != 0) pc = Label(operand); break;
                case Opcodes.Call:
                    if (calls.Count == 3) throw new InvalidOperationException("call deeper than 3");
                    calls.Push((w, pc));
                    (w, pc) = (operand, (int)image.Workers[operand].Entries[0]);
                    break;
                case Opcodes.Ret:
                    if (calls.Count == 0) return steps;
                    (w, pc) = calls.Pop();
                    break;
                case Opcodes.End or Opcodes.End1: return steps;
                case Opcodes.EndP or Opcodes.EndP1: Pop(); return steps;
                case Opcodes.Func or Opcodes.FuncFacc: {
                    int argc = ScriptCompiler.Natives[(ushort)operand]?.Argc ?? throw new InvalidOperationException($"native {operand:x4}");
                    Value[] args = new Value[argc];
                    for (int k = argc - 1; k >= 0; k--) args[k] = Pop();
                    Calls.Add(((ushort)operand, args));
                    Value result = native?.Invoke((ushort)operand, args) ?? Value.Int(0);
                    if (op == Opcodes.Func) Push(result);
                    break;
                }
                case Opcodes.Req0 or Opcodes.ReqW0 or Opcodes.ReqE0 or >= Opcodes.Req1 and < Opcodes.Op54: {
                    int e = Pop().AsInt, target = Pop().AsInt, level = Pop().AsInt;
                    Requests.Add((op, level, target, e));
                    if (RunRequests) steps += Run(target, e, limit - steps);
                    Push(Value.Int(1));
                    break;
                }
                case >= Opcodes.PopI0 and < Opcodes.PopF0: _ints[op - Opcodes.PopI0] = Value.Int(Pop().AsInt); break;
                case >= Opcodes.PopF0 and < Opcodes.PopF0 + 10: _floats[op - Opcodes.PopF0] = Value.Float(Pop().AsFloat); break;
                case >= Opcodes.PushI0 and < Opcodes.PushF0: Push(_ints[op - Opcodes.PushI0]); break;
                case >= Opcodes.PushF0 and < Opcodes.PushF0 + 10: Push(_floats[op - Opcodes.PushF0]); break;
                default: throw new InvalidOperationException($"opcode {op:x2} at {at} is not in the test machine");
            }
        }
    }
}
