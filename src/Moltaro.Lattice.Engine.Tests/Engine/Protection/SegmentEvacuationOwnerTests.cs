using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.SegmentEvacuationTests;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

public sealed class SegmentEvacuationOwnerTests
{
    private const int OwnerKey = 701;

    [Theory]
    [InlineData(LatticeRoleEnum.Master, "Text", Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Child, "Text", Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Master, "Records", Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Child, "Records", Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Master, "Lookup", Skip = PendingSegmentEvacuation)]
    [InlineData(LatticeRoleEnum.Child, "Lookup", Skip = PendingSegmentEvacuation)]
    public async Task Large_text_records_and_lookup_chunks_republish_their_real_owning_edge(LatticeRoleEnum role, string kind)
    {
        const int sourceFill = 201;
        const int reserveKey = 301;
        var (declaration, value, warmBytes) = kind switch
        {
            "Text" => (""" "Type":"String","StringStorage":"Dictionary" """,
                (object)new string('q', ValueBlocks.ChunkPayload), 0),
            "Records" => (""" "Type":"List","Element":{"Type":"String"} """,
                Strings(ValueBlocks.ChunkPayload / (ValueBlocks.DescriptorSize + ValueBlocks.InlineLimit) + 19), 1_600),
            "Lookup" => (""" "Type":"Map","Key":{"Type":"Int32"},"Value":{"Type":"Int32"} """,
                Enumerable.Range(0, 5_800).ToDictionary(key => key, key => key * -11), 784),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        var schema = LatticeSchema.Compile($$"""
            {"StringEncoding":"Utf8","EntityTypes":[
              {"Id":1,"Name":"Item","Fields":[
                {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Code","Type":"Int8"},
                {"Id":3,"Name":"Data","Type":"Binary"}],"PrimaryKey":[1]},
              {"Id":2,"Name":"Owner","Fields":[
                {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Code","Type":"Int8"},
                {"Id":3,"Name":"Data",{{declaration}}}],"PrimaryKey":[1]}]}
            """);
        await using var master = await ReplicationNodes.OpenMasterAsync(schema,
            role == LatticeRoleEnum.Master ? Inputs(3, allocator, samples.Add) : Inputs(3));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? Inputs(3, allocator, samples.Add) : Inputs(3));
        var node = role == LatticeRoleEnum.Master ? master : child;
        if (warmBytes != 0)
        {
            await InsertAsync(master, 501, 1, warmBytes);
        }

        var anchorCount = AnchorChunks - (warmBytes == 0 ? 0 : 1);
        await InsertAsync(master, 1, anchorCount);
        var owner = master.Set("Owner");
        Assert.True((await owner.InsertAsync(new LatticeRecord(owner.EntityType)
        {
            ["Id"] = (long)OwnerKey,
            ["Code"] = (sbyte)25,
            ["Data"] = value,
        }).AsTask().WaitAsync(Bound)).Changed);
        await child.WaitForPositionAsync(master.Position, Bound);

        var before = Inspect(node, kind);
        var other = Locations(node);
        var anchorSegment = other[1].Chunk.Segment;
        Assert.Equal(anchorSegment, before.Root.Segment);
        Assert.Equal(anchorSegment, before.Owner.Segment);
        Assert.NotEqual(anchorSegment, before.Chunk.Segment);
        var liveRegions = kind == "Lookup" ? 2 : 1;
        Assert.Equal(liveRegions, SegmentAt(before.Arena, before.Chunk.Segment).RegionsInUse);
        var fillCount = RegionsPerSegment - liveRegions;
        await InsertAsync(master, sourceFill, fillCount);
        await InsertAsync(master, reserveKey, 1);
        await child.WaitForPositionAsync(master.Position, Bound);
        var full = Locations(node);
        var sourceNative = NativeOf(allocator, full[sourceFill]);
        var reserveNative = NativeOf(allocator, full[reserveKey]);
        Assert.Equal(before.Chunk.Segment, full[sourceFill].Chunk.Segment);
        var anchorHoles = Enumerable.Range(anchorCount - liveRegions + 1, liveRegions).ToArray();
        var destinations = anchorHoles.Select(key => full[key].Chunk).ToArray();
        await DeleteAsync(master, Enumerable.Range(sourceFill, fillCount).Concat(anchorHoles).Append(reserveKey));
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();

        Assert.Equal(before, Inspect(node, kind));
        Assert.Equal(liveRegions, SegmentAt(before.Arena, before.Chunk.Segment).RegionsInUse);
        Assert.All(destinations, address => Assert.Throws<ArgumentException>("address", () => _ = before.Arena.Header(address)));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        var revision = ExpectOwner(node.Set("Owner"), value);
        var position = node.Position;
        var requests = allocator.Requests;
        var returns = allocator.Returned;

        await RunTurnsAsync(node, TurnsPerSweep);
        var current = Inspect(node, kind);
        Assert.True(before.Chunk != current.Chunk, $"The large {kind} chunk stayed at {before.Chunk}.");
        Assert.Equal(before.Root, current.Root);
        Assert.Equal(before.Owner, current.Owner);
        Assert.Equal(before.Locator, current.Locator);
        Assert.Contains(current.Chunk, destinations);
        var move = Assert.Single(samples, sample => sample.Source == before.Chunk);
        Assert.Equal((before.BlockKind, before.Flags, before.EdgeKind), (move.SourceKind, move.SourceFlags, move.SourceDescriptorKind));
        Assert.Equal(liveRegions, samples.Count(sample => sample.SlotBytes != 0));
        Assert.Equal(RegionBytes, move.SlotBytes);
        Assert.Equal(revision, ExpectOwner(node.Set("Owner"), value));
        Assert.Equal(position, node.Position);
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(returns + 1, allocator.Returned);
        Assert.Equal(SegmentBytes, samples.Sum(sample => sample.ReturnedNativeBytes));
        Assert.DoesNotContain(sourceNative, Outstanding(allocator));
        Assert.Contains(reserveNative, Outstanding(allocator));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        OwnershipAudit.Expect(node);
    }

    private static OwnerLocation Inspect(EngineDatabase node, string kind)
    {
        OwnerLocation? located = null;
        node.ForEachLiveRoot((arena, layout, handle, root) =>
        {
            if (layout.EntityType.Name != "Owner")
            {
                return;
            }

            var field = layout.StoredFieldOf(layout.EntityType.GetField("Data"));
            var descriptor = ResidentDescriptor.Read(arena.Payload(root)[(field.Offset - BlockHeader.Size)..]);
            var owner = ValueBlocks.AddressOf(descriptor);
            var flags = (ushort)0;
            var edge = DescriptorKindEnum.Block;
            var headerKind = BlockKindEnum.Chunk;
            ArenaAddress chunk;
            switch (kind)
            {
                case "Text":
                    Assert.Equal(DescriptorKindEnum.Text, descriptor.Kind);
                    owner = ValueBlocks.AddressOf(ValueBlocks.ResolveText(arena, descriptor));
                    Assert.Equal(BlockKindEnum.SharedTextDirectory, arena.Header(owner).Kind);
                    chunk = ValueBlocks.AddressOf(ResidentDescriptor.ReadAligned(arena.Payload(owner)));
                    headerKind = BlockKindEnum.SharedTextChunk;
                    Assert.Equal(new RootHandle(0, (uint)descriptor.Location), arena.Header(chunk).Root);
                    Assert.Equal(default, arena.Header(chunk).Parent);
                    break;
                case "Records":
                    Assert.Equal(DescriptorKindEnum.Records, descriptor.Kind);
                    Assert.Equal(BlockKindEnum.ChunkDirectory, arena.Header(owner).Kind);
                    chunk = ValueBlocks.AddressOf(ResidentDescriptor.ReadAligned(arena.Payload(owner)));
                    flags = BlockHeader.RecordBlockFlag;
                    Assert.Equal((handle, owner, (uint)BlockHeader.Size),
                        (arena.Header(chunk).Root, arena.Header(chunk).Parent, arena.Header(chunk).DescriptorOffset));
                    break;
                default:
                    Assert.Equal(DescriptorKindEnum.Lookup, descriptor.Kind);
                    Assert.Equal(BlockKindEnum.Lookup, arena.Header(owner).Kind);
                    chunk = CollectionLookup.ReadChunk(CollectionLookup.Chunks(arena, owner), 0);
                    flags = BlockHeader.LookupSlotsFlag;
                    edge = default;
                    Assert.Equal((handle, owner, (uint)CollectionLookup.FirstChunkOffset),
                        (arena.Header(chunk).Root, arena.Header(chunk).Parent, arena.Header(chunk).DescriptorOffset));
                    break;
            }

            var header = arena.Header(chunk);
            Assert.Equal((headerKind, flags), (header.Kind, header.Flags));
            Assert.Equal(RegionBytes, Arena.BlockSizeFor((int)header.PayloadLength));
            Assert.Equal(0u, chunk.Offset % RegionBytes);
            located = new OwnerLocation(arena, root, owner, chunk, descriptor.Location, headerKind, flags, edge);
        });
        return Assert.IsType<OwnerLocation>(located);
    }

    private static LatticeRevision ExpectOwner(ILatticeSet items, object value)
    {
        Assert.True(items.TryGet((long)OwnerKey, out var record, out var revision));
        Assert.Equal((sbyte)25, record.Get<sbyte>("Code"));
        switch (value)
        {
            case string text:
                Assert.Equal(text, record.Get<string>("Data"));
                break;
            case List<string> records:
                Assert.Equal(records, record.Get<List<string>>("Data"));
                break;
            case Dictionary<int, int> map:
                Assert.Equal(map.OrderBy(entry => entry.Key), record.Get<Dictionary<int, int>>("Data")!.OrderBy(entry => entry.Key));
                break;
        }

        return revision;
    }

    private static List<string> Strings(int count) => Enumerable.Range(0, count)
        .Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(64, 'r')).ToList();

    private sealed record OwnerLocation(Arena Arena, ArenaAddress Root, ArenaAddress Owner, ArenaAddress Chunk,
        ulong Locator, BlockKindEnum BlockKind, ushort Flags, DescriptorKindEnum EdgeKind);
}
