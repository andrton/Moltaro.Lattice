using System.Runtime.InteropServices;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Collections;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Layouts;

// Both resident forms represent the same entries. Five through eight entries can occupy either
// form. A whole write selects compact at four (physical-storage.md, "Map and Set lookup layout").
public sealed class CollectionFormTests
{
    [Theory]
    [InlineData(false, 4)]
    [InlineData(false, 5)]
    [InlineData(false, 6)]
    [InlineData(false, 7)]
    [InlineData(false, 8)]
    [InlineData(false, 9)]
    [InlineData(true, 4)]
    [InlineData(true, 5)]
    [InlineData(true, 6)]
    [InlineData(true, 7)]
    [InlineData(true, 8)]
    [InlineData(true, 9)]
    public void Compact_and_hashed_forms_find_equal_contents_and_a_replacement_with_four_entries_is_compact(bool map, int count)
    {
        using var item = RootArena.Of(map
            ? "{ 'Id': 2, 'Name': 'Values', 'Type': 'Map', 'Key': { 'Type': 'UInt8' }, 'Value': { 'Type': 'UInt8' } }"
            : "{ 'Id': 2, 'Name': 'Values', 'Type': 'Set', 'Element': { 'Type': 'UInt8' } }");
        var field = item.Field("Values");
        var larger = count == 4 ? item.Publish(Write(item, map, 9)) : default;
        if (!larger.IsNull)
        {
            Assert.Equal(DescriptorKindEnum.Lookup, ValueBlocks.Read(item.Arena.Payload(larger)[sizeof(long)..]).Kind);
        }

        var written = Write(item, map, count);
        var descriptor = ValueBlocks.Read(item.Arena.Payload(written)[sizeof(long)..]);

        Assert.Equal(count >= 9 ? DescriptorKindEnum.Lookup : DescriptorKindEnum.Inline, descriptor.Kind);
        AssertMatches(item.Reader(written), field, count);

        if (count is > 4 and < 9)
        {
            // A resident lookup from the larger form may have capacity left. Construct it
            // without the whole-write selector, which always chooses compact for these counts.
            var hashed = ResidentLookup(item, map, count);
            AssertMatches(item.Reader(hashed), field, count);
            RootStorage.Free(item.Arena, item.Layout, hashed);
        }

        if (!larger.IsNull)
        {
            RootStorage.Free(item.Arena, item.Layout, larger);
        }

        RootStorage.Free(item.Arena, item.Layout, written);
        Assert.Equal(0, item.Arena.Counters.PreparedBytes);
    }

    private static ArenaAddress Write(RootArena item, bool map, int count)
    {
        var keys = Enumerable.Range(0, count).Select(number => (byte)number);
        object values = map ? keys.ToDictionary(key => key, key => (byte)(key + 20)) : keys.ToHashSet();
        var writer = item.Writer();
        RootValues.Write(ref writer, item.EntityType, [1L, values]);

        return writer.Complete();
    }

    private static ArenaAddress ResidentLookup(RootArena item, bool map, int count)
    {
        const int SlotCount = 14;
        var arena = item.Arena;
        var width = map ? 2 : 1;
        var bytes = count * width;
        Assert.True(arena.TryAllocatePrepared(sizeof(long) + 16, BlockKindEnum.Root, RootArena.Handle, out var root));
        Assert.True(arena.TryAllocatePrepared(16 + SlotCount * sizeof(ulong), BlockKindEnum.Lookup, RootArena.Handle, out var lookup));
        Assert.True(arena.TryAllocatePrepared(bytes, BlockKindEnum.Value, RootArena.Handle, out var entries));
        var rootPayload = arena.Payload(root);
        MemoryMarshal.Write(rootPayload, 1L);
        ValueBlocks.Write(rootPayload[sizeof(long)..], new ValueDescriptor { Kind = DescriptorKindEnum.Lookup, Length = (uint)count, Location = lookup.Value });
        var directory = arena.Payload(lookup);
        directory.Clear();
        ValueBlocks.Write(directory, new ValueDescriptor { Kind = DescriptorKindEnum.Block, Length = (uint)bytes, Location = entries.Value });
        var slots = MemoryMarshal.Cast<byte, ulong>(directory[16..]);

        arena.Header(root).LayoutVersion = item.Layout.LayoutVersion;
        arena.Header(lookup).LayoutVersion = item.Layout.LayoutVersion;
        arena.Header(entries).LayoutVersion = item.Layout.LayoutVersion;
        arena.Header(lookup).Parent = root;
        arena.Header(lookup).DescriptorOffset = 32 + sizeof(long);
        arena.Header(entries).Parent = lookup;
        arena.Header(entries).DescriptorOffset = 32;
        var hashes = new PrimaryKeys(item.EntityType, item.Layout.StringEncoding, seed: 0);

        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var entry = arena.Payload(entries)[(ordinal * width)..];
            entry[0] = (byte)ordinal;
            if (map)
            {
                entry[1] = (byte)(ordinal + 20);
            }

            var hash = hashes.IntegerHash(ordinal);
            var home = (int)(((ulong)hash * SlotCount) >> 32);
            var slot = home;
            while (slots[slot] != 0)
            {
                slot = (slot + 1) % SlotCount;
            }

            slots[slot] = ((ulong)hash << 32) | (uint)(ordinal + 1);
        }

        return root;
    }

    private static void AssertMatches(in RootReader root, LatticeField field, int count)
    {
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            Assert.Equal(ordinal, root.FindEntry(field, CollectionProbes.Bind(field, (byte)ordinal)));
        }

        Assert.Equal(-1, root.FindEntry(field, CollectionProbes.Bind(field, (byte)count)));
    }
}
