// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Ffx.Atel.Bytecode.Text;

namespace Ffx.Atel.Loader.Tests;

/// <summary>Injected messages in a table the game reads through a pointer: the event's, the battle's, system_01's.</summary>
public sealed class MessageSlotTests {
    /// <summary>Game memory as regions by address, and the one pointer the game reads.</summary>
    private sealed class FakeMemory : ITableMemory {
        public readonly Dictionary<nint, byte[]> Regions = [];
        public nint Pointer;
        private nint _next = 0x10000;

        public nint Get() => Pointer;
        public void Set(nint table) => Pointer = table;

        private (byte[] Bytes, int At) Find(nint at) {
            foreach ((nint start, byte[] bytes) in Regions) {
                if (at >= start && at < start + bytes.Length) return (bytes, (int)(at - start));
            }
            throw new InvalidOperationException($"0x{at:x} is not mapped");
        }

        public ushort U16(nint at) { (byte[] b, int o) = Find(at); return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)); }
        public byte U8(nint at) { (byte[] b, int o) = Find(at); return b[o]; }
        public byte[] Read(nint at, int length) { (byte[] b, int o) = Find(at); return b[o..(o + length)]; }

        public nint Allocate(byte[] bytes) {
            nint at = _next;
            _next += 0x10000;
            Regions[at] = bytes;
            return at;
        }

        public void Free(nint at) => Assert.True(Regions.Remove(at));

        /// <summary>The game loads a table at <paramref name="at"/> and points at it.</summary>
        public void Load(nint at, byte[] table) {
            Regions[at] = [.. table, 0x55, 0x55];
            Pointer = at;
        }

        public IReadOnlyList<Message> Current => MessageTable.Parse(Read(Pointer, Injection.TableLength(o => U16(Pointer + o), o => U8(Pointer + o))!.Value)).Messages;
    }

    private static byte[] Table(params byte[] first) => new MessageTable([new Message(first, 0), new Message([0x31], 0)]).Write();

    [Fact]
    public void TheBattlesTableGetsTheMessagesAndAgainAfterTheNextLoad() {
        FakeMemory memory = new();
        MessageSlot slot = new("battle");
        Assert.Null(slot.Apply(memory, 1, [(3, [0x40])]));      // no battle table yet
        memory.Load(0x1000, Table(0x30));
        Assert.StartsWith("battle kami00_00: 1 message(s) set", slot.Apply(memory, 1, [(3, [0x40])], "battle kami00_00"), StringComparison.Ordinal);
        Assert.NotEqual(0x1000, memory.Pointer);
        Assert.Equal(4, memory.Current.Count);
        Assert.Equal([0x40], memory.Current[3].Text);
        Assert.Equal([0x30], memory.Current[0].Text);
        Assert.Null(slot.Apply(memory, 1, [(3, [0x40])]));      // nothing changed

        // The next battle: the game loads its own table (the read frees the last one) and points at it.
        memory.Regions.Remove(0x1000);
        memory.Load(0x2000, Table(0x32));
        Assert.NotNull(slot.Apply(memory, 1, [(3, [0x40])]));
        Assert.Equal([0x32], memory.Current[0].Text);
        Assert.Equal([0x40], memory.Current[3].Text);
        Assert.Equal(2, slot.Allocated);                         // the last copy stays for a window still showing it

        // The package changed: a new copy of the game's table.
        Assert.NotNull(slot.Apply(memory, 2, [(1, [0x41])]));
        Assert.Equal([0x41], memory.Current[1].Text);
        Assert.Equal(2, memory.Current.Count);

        // The package went away: the game's table again.
        Assert.NotNull(slot.Apply(memory, 3, null));
        Assert.Equal(0x2000, memory.Pointer);
        Assert.Equal(0, slot.Installed);
    }

    [Fact]
    public void ATableThatIsNotOneIsAnError() {
        FakeMemory memory = new();
        memory.Load(0x1000, [3, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Throws<InvalidDataException>(() => new MessageSlot("battle").Apply(memory, 1, [(0, [0x40])]));
        Assert.Equal(0x1000, memory.Pointer);
    }

    [Fact]
    public void TheBattleTableIsReadOnlyOnceTheBattleSetItUp() {
        // g_BtlStatus: 0 no battle, 4-7 the read (5 frees the last text, 7 sets the pointer), then the battle.
        Assert.False(BattleTables.Ready(0));
        foreach (sbyte s in new sbyte[] { 4, 5, 6, 7 }) Assert.False(BattleTables.Ready(s));
        foreach (sbyte s in new sbyte[] { 1, 2, 3, 8, 9, 0x0a, 0x15, 0x16, 0x17, 0x21, 0x22 }) Assert.True(BattleTables.Ready(s));
        Assert.True(BattleTables.IsBattle("kami00_00", "KAMI00_00"));
        Assert.False(BattleTables.IsBattle("kami00_00", "kami00_01"));
    }
}
