using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Layouts;

// Physical repair changes only the location word of a published descriptor. It must remain
// inside the 16-byte slot and naturally aligned in a packed native block. The engine contract
// ("Collections and strings") leaves the logical value and inline offsets intact.
public sealed class ResidentDescriptorTests
{
    internal const string Pending = "The resident descriptor layout is not implemented yet.";

    [Theory(Skip = Pending)]
    [InlineData(0, 8, 4)]
    [InlineData(1, 7, 3)]
    [InlineData(2, 6, 2)]
    [InlineData(3, 5, 1)]
    [InlineData(4, 4, 0)]
    [InlineData(5, 3, 11)]
    [InlineData(6, 2, 10)]
    [InlineData(7, 1, 9)]
    public void Each_native_slot_keeps_its_size_and_metadata_and_publishes_one_aligned_location(
        int skew, int locationAt, int lengthAt)
    {
        const int SlotSize = 16;
        using var item = RootArena.Of("");
        var arena = item.Arena;
        Assert.True(arena.TryAllocatePrepared(56, BlockKindEnum.Value, RootArena.Handle, out var holder));
        Assert.True(arena.TryAllocatePrepared(100, BlockKindEnum.Value, RootArena.Handle, out var original));
        Assert.True(arena.TryAllocatePrepared(100, BlockKindEnum.Value, RootArena.Handle, out var replacement));
        var payload = arena.Payload(holder);
        var first = 8 + skew;
        var next = first + SlotSize;
        Assert.Equal(SlotSize, (int)Unsafe.ByteOffset(ref payload[first], ref payload[next]));

        foreach (var descriptor in Forms(original))
        {
            payload.Fill(0xA9);
            var place = payload.Slice(first, SlotSize);
            var neighbor = payload.Slice(next, SlotSize);

            ResidentDescriptor.Write(place, descriptor);
            ResidentDescriptor.Write(neighbor, new ValueDescriptor { Kind = DescriptorKindEnum.Inline, Length = 4, Location = 77 });

            var decoded = ResidentDescriptor.Read(place);
            Assert.Equal((descriptor.Kind, descriptor.Length, descriptor.Location), (decoded.Kind, decoded.Length, decoded.Location));
            var neighborRead = ResidentDescriptor.Read(neighbor);
            Assert.Equal((DescriptorKindEnum.Inline, 4U, 77UL), (neighborRead.Kind, neighborRead.Length, neighborRead.Location));
            Assert.Equal(0, (BlockHeader.Size + first + locationAt) % sizeof(ulong));
            Assert.Equal(descriptor.Location, BinaryPrimitives.ReadUInt64LittleEndian(place[locationAt..]));

            var rawLength = BinaryPrimitives.ReadUInt32LittleEndian(place[lengthAt..]);
            if (skew == 0)
            {
                Assert.Equal((uint)descriptor.Kind, BinaryPrimitives.ReadUInt32LittleEndian(place));
                Assert.Equal(descriptor.Length, rawLength);
            }
            else
            {
                Assert.Equal((descriptor.Length, (uint)descriptor.Kind), (rawLength >> 4, rawLength & 0xF));
            }

            Assert.Equal(descriptor.Length, ResidentDescriptor.ReadLength(place, BlockHeader.Size + first));
            Assert.Equal(0xA9, payload[first - 1]);
            Assert.Equal(0xA9, payload[next + SlotSize]);

            if (descriptor.Kind is DescriptorKindEnum.Null or DescriptorKindEnum.Inline || descriptor.Location == 0)
            {
                continue;
            }

            var expected = payload.ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(first + locationAt), replacement.Value);
            ResidentDescriptor.PublishLocation(place, replacement);

            Assert.True(payload.SequenceEqual(expected));
            var moved = ResidentDescriptor.Read(place);
            Assert.Equal((descriptor.Kind, descriptor.Length, replacement.Value), (moved.Kind, moved.Length, moved.Location));
        }
    }

    [Fact(Skip = Pending)]
    public void Lengths_in_a_skewed_fixed_part_copy_use_the_offsets_of_the_native_root()
    {
        using var item = RootArena.Of("""
            { 'Id': 2, 'Name': 'Flag', 'Type': 'Int8' },
            { 'Id': 3, 'Name': 'Data', 'Type': 'Binary' },
            { 'Id': 4, 'Name': 'Label', 'Type': 'String' }
            """);
        var writer = item.Writer();
        RootValues.Write(ref writer, item.EntityType, [123L, (sbyte)17, new byte[313], "label"]);
        var root = item.Publish(writer.Complete());
        var data = item.Field("Data");
        var label = item.Field("Label");
        var dataAt = item.Layout.StoredFieldOf(data).Offset;
        Assert.Equal(41, dataAt);

        var native = item.Arena.Payload(root);
        var fixedLength = RootReader.FixedPartLength(item.Layout);
        Span<byte> scratch = stackalloc byte[fixedLength + 8];
        // Choose the skew from the actual references, independent of stack allocation alignment.
        var actualSkew = (int)(Unsafe.ByteOffset(ref MemoryMarshal.GetReference(native), ref MemoryMarshal.GetReference(scratch)) & 7);
        var copy = scratch.Slice((3 - actualSkew) & 7, fixedLength);
        Assert.Equal(3, (int)(Unsafe.ByteOffset(ref MemoryMarshal.GetReference(native), ref MemoryMarshal.GetReference(copy)) & 7));

        var original = item.Reader(root);
        original.CopyFixedPart(copy);
        Assert.True(copy.SequenceEqual(native[..fixedLength]));
        var dataPlace = copy[(dataAt - BlockHeader.Size)..];
        Assert.Equal((313U << 4) | 2U, BinaryPrimitives.ReadUInt32LittleEndian(dataPlace[3..]));
        Assert.Equal(313U, ResidentDescriptor.ReadLength(dataPlace, dataAt));

        var copied = new RootReader(item.Layout, copy);
        Assert.Equal(123L, copied.GetInteger(item.Field("Id")));
        Assert.Equal(313, copied.GetLength(data));
        Assert.Equal(5, copied.GetLength(label));

        RootStorage.Free(item.Arena, item.Layout, root);
    }

    private static ValueDescriptor[] Forms(ArenaAddress address) =>
    [
        default,
        new() { Kind = DescriptorKindEnum.Null, Length = 268_173_376 },
        new() { Kind = DescriptorKindEnum.Inline, Length = 7, Location = 123 },
        new() { Kind = DescriptorKindEnum.Block, Length = 100, Location = address.Value },
        new() { Kind = DescriptorKindEnum.Chunked, Length = 268_173_376, Location = address.Value },
        new() { Kind = DescriptorKindEnum.Text, Length = 38_105, Location = address.Value },
        new() { Kind = DescriptorKindEnum.Records },
        new() { Kind = DescriptorKindEnum.Records, Length = 3_230_166, Location = address.Value },
        new() { Kind = DescriptorKindEnum.Lookup, Length = 23_465_170, Location = address.Value },
    ];
}
