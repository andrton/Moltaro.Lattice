using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Views;

// A protected read can keep the old span when the parent edge publishes a new block.
// A protected read that opens the edge later follows the new block. Neither the revision
// nor the canonical body changes. See milestone-1-engine-contract.md, "Collections and strings".
public sealed class ResidentDescriptorViewTests
{
    private static readonly LatticeSchema Schema = ItemSchema.Compile("""
        { 'Id': 2, 'Name': 'Code', 'Type': 'Int8' },
        { 'Id': 3, 'Name': 'Data', 'Type': 'Binary' },
        { 'Id': 4, 'Name': 'Label', 'Type': 'String' },
        { 'Id': 5, 'Name': 'Empty', 'Type': 'Binary' },
        { 'Id': 6, 'Name': 'Absent', 'Type': 'String', 'Nullable': true }
        """);

    [Fact]
    public async Task A_held_view_and_export_follow_a_published_edge_without_invalidating_an_older_span()
    {
        var options = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = 32L * Arena.MaxBlockSize };
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, new EngineTestInputs { Arena = options });
        var items = master.Set("Item");
        var data = Enumerable.Range(0, 1_000).Select(index => (byte)(7 * index + 3)).ToArray();
        var record = ItemOf(items.EntityType, 1L, data);
        await items.InsertAsync(record);
        Assert.True(items.TryGet(1L, out _, out var revision));

        (Arena Arena, RootLayout Layout, ArenaAddress Root) source = default;
        master.ForEachLiveRoot((arena, layout, _, root) => source = (arena, layout, root));
        var (storage, stored, address) = source;
        Assert.NotNull(storage);
        Assert.NotNull(stored);
        Assert.False(address.IsNull);

        var place = stored.StoredFieldOf(items.EntityType.GetField("Data")).Offset;
        Assert.Equal(42, place);
        var expected = RootValues.Export(stored, items.EntityType, [1L, (sbyte)27, data, "tri", Array.Empty<byte>(), null]).ToArray();
        AssertCanonicalBody(expected, data);
        var stable = default(ArenaAddress);
        var moved = default(ArenaAddress);

        // Read the initial edge under the engine's ordinary epoch through a view.
        Assert.True(items.TryView(1L, view =>
        {
            var payload = storage.Payload(address);
            // The Int64 key has the same bytes as the transferred key. The neighboring Binary
            // descriptor instead stores combined metadata at payload offset 12, two bytes into
            // the native slot. Its transferred length is four bytes into the canonical slot.
            Assert.Equal(1L, MemoryMarshal.Read<long>(payload[1..]));
            Assert.Equal(((uint)data.Length << 4) | 2U, MemoryMarshal.Read<uint>(payload[12..]));
            var descriptor = ResidentDescriptor.Read(payload[(place - BlockHeader.Size)..]);
            Assert.Equal(DescriptorKindEnum.Block, descriptor.Kind);
            var initial = ValueBlocks.AddressOf(descriptor);
            AssertBorrowed(view["Data"].GetPart(0), storage, initial, data);
            return initial;
        }, out stable));
        Assert.False(stable.IsNull);

        using var exportPause = new ViewerPause();
        using var openingPause = new ViewerPause();
        var output = new PausedExportBuffer(expected.Length, storage.Payload(address), exportPause);

        var exporting = OwnThread.Run(() =>
        {
            Assert.True(items.TryView(1L, view =>
            {
                var original = view["Data"].GetPart(0);
                AssertBorrowed(original, storage, stable, data);
                BlockExport.Write(output, storage, stored, address);
                AssertBorrowed(original, storage, stable, data);
                AssertBorrowed(view["Data"].GetPart(0), storage, moved, data);
                Assert.Equal(expected.Length, BlockExport.Length(storage, stored, address));
                return view.Revision;
            }, out var seen));
            return seen;
        });
        await exportPause.EnteredAsync(exporting);

        var opening = OwnThread.Run(() =>
        {
            Assert.True(items.TryView(1L, view =>
            {
                openingPause.Hold();
                AssertBorrowed(view["Data"].GetPart(0), storage, moved, data);
                var active = ResidentDescriptor.Read(storage.Payload(address)[(place - BlockHeader.Size)..]);
                Assert.Equal(moved.Value, active.Location);
                return view.Revision;
            }, out var seen));
            return seen;
        });
        await openingPause.EnteredAsync(opening);

        // An unrelated write supplies the normal writer lock for the test publication.
        // The old block remains allocated until both callbacks have left their epochs.
        master.ArmPublicationSwitch(PublicationPointEnum.BeforePosition, () =>
        {
            moved = CopyToAnotherSegment(storage, stable);
            ResidentDescriptor.PublishLocation(storage.Payload(address)[(place - BlockHeader.Size)..], moved);
        });
        await items.InsertAsync(ItemOf(items.EntityType, 2L, [])).AsTask().WaitAsync(Bound);
        Assert.NotEqual(stable.Segment, moved.Segment);
        Assert.NotEqual(stable.Offset, moved.Offset);

        exportPause.Release();
        openingPause.Release();
        Assert.Equal(revision, await exporting.WaitAsync(Bound));
        Assert.Equal(revision, await opening.WaitAsync(Bound));
        Assert.Equal(expected, output.Written.ToArray());

        master.ArmPublicationSwitch(PublicationPointEnum.BeforePosition, () => storage.Free(stable));
        await items.DeleteAsync(2L).AsTask().WaitAsync(Bound);

        // Give a real child the exact body produced by the paused export.
        await using var relay = FrameRelay.Start(master.ListenUri!, frame => WithResumedExport(frame, output.Written));
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var childItems = child.Set("Item");
        Assert.True(childItems.TryGet(1L, out var childItem, out var childRevision));
        Assert.Equal(revision, childRevision);
        Assert.Equal(data, childItem.Get<byte[]>("Data"));
        Assert.Equal((sbyte)27, childItem.Get<sbyte>("Code"));
        Assert.Equal("tri", childItem.Get<string>("Label"));
        Assert.Empty(childItem.Get<byte[]>("Empty")!);
        Assert.Null(childItem.Get<string>("Absent"));
        Assert.Equal(master.Position, child.Position);
    }

    private static LatticeRecord ItemOf(LatticeEntityType type, long id, byte[] data) => new(type)
    {
        ["Id"] = id,
        ["Code"] = (sbyte)27,
        ["Data"] = data,
        ["Label"] = "tri",
        ["Empty"] = Array.Empty<byte>(),
        ["Absent"] = null,
    };

    private static ArenaAddress CopyToAnotherSegment(Arena arena, ArenaAddress source)
    {
        var original = arena.Header(source);
        var length = (int)original.PayloadLength;
        var fillers = new List<ArenaAddress>();
        // At most one region's slots remain in the source segment. If the first
        // slot of the next segment has the same offset, keep it and take the next.
        var most = (Arena.MaxBlockSize / Arena.BlockSizeFor(length)) + 2;
        try
        {
            for (var attempt = 0; attempt < most; attempt++)
            {
                Assert.True(arena.TryAllocatePrepared(length, original.Kind, original.Root, out var destination));
                if (destination.Segment == source.Segment || destination.Offset == source.Offset)
                {
                    fillers.Add(destination);
                    continue;
                }

                arena.Header(destination) = original;
                arena.Payload(source).CopyTo(arena.Payload(destination));
                arena.Publish(destination);
                return destination;
            }
        }
        finally
        {
            foreach (var filler in fillers)
            {
                arena.Free(filler);
            }
        }

        throw new InvalidOperationException("The arena did not offer another segment and offset.");
    }

    private static void AssertBorrowed(ReadOnlySpan<byte> part, Arena arena, ArenaAddress source, byte[] expected)
    {
        var native = arena.Payload(source);
        Assert.True(part.SequenceEqual(expected));
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(native), ref MemoryMarshal.GetReference(part)));
    }

    private static void AssertCanonicalBody(ReadOnlySpan<byte> body, byte[] data)
    {
        // The version precedes one bitmap byte, an Int64 and an Int8. The four
        // wire slots end at block offset 106, from which the inline area starts.
        Assert.Equal(4 + 74 + 3 + data.Length, body.Length);
        Assert.Equal(1, body[4]);
        Assert.Equal(1L, MemoryMarshal.Read<long>(body[5..]));
        Assert.Equal(27, body[13]);
        AssertCanonicalSlot(body[14..], 2, (uint)data.Length, 0);
        AssertCanonicalSlot(body[30..], 1, 3, 106);
        AssertCanonicalSlot(body[46..], 1, 0, 109);
        AssertCanonicalSlot(body[62..], 0, 0, 0);
        Assert.True(body.Slice(78, 3).SequenceEqual("tri"u8));
        Assert.True(body[81..].SequenceEqual(data));
    }

    private static void AssertCanonicalSlot(ReadOnlySpan<byte> slot, uint kind, uint length, ulong location)
    {
        Assert.Equal(kind, MemoryMarshal.Read<uint>(slot));
        Assert.Equal(length, MemoryMarshal.Read<uint>(slot[4..]));
        Assert.Equal(location, MemoryMarshal.Read<ulong>(slot[8..]));
    }

    private static WireFrame[] WithResumedExport(WireFrame frame, ReadOnlyMemory<byte> body)
    {
        if (frame.Kind != FrameKindEnum.Blocks)
        {
            return [frame];
        }

        var payload = frame.Payload.ToArray();
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        Assert.Equal((ulong)(RootEnvelope.Size + body.Length), BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(sizeof(uint))));
        var target = payload.AsSpan(sizeof(uint) + sizeof(ulong) + RootEnvelope.Size);
        Assert.True(target.SequenceEqual(body.Span));
        body.Span.CopyTo(target);
        return [frame.WithPayload(payload)];
    }
}
