using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.SegmentEvacuationTests;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Moving a root preserves every separately allocated child's owning edge
// and keeps the previous root readable through its original epoch.
public sealed class SegmentEvacuationFanoutTests
{
    private const int Fields = 4_000;
    private const int ChildLength = 65;
    private const int ChildSlot = 112;
    private const int ChildRegions = (Fields + RegionBytes / ChildSlot - 1) / (RegionBytes / ChildSlot);
    private const int AnchorFill = AnchorChunks - ChildRegions - 1;
    private const long WideKey = 701;
    private const sbyte WrittenCode = -19;

    private static readonly LatticeSchema WideSchema = MakeSchema();

    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task Evacuating_a_wide_root_adopts_all_four_thousand_owning_edges(LatticeRoleEnum role)
    {
        const int sourceFill = 101;
        const int reserveKey = 301;
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        await using var master = await ReplicationNodes.OpenMasterAsync(WideSchema,
            role == LatticeRoleEnum.Master ? Inputs(3, allocator, samples.Add) : Inputs(3));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? Inputs(3, allocator, samples.Add) : Inputs(3));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var set = master.Set("Wide");
        var wide = node.Set("Wide");
        var record = new LatticeRecord(set.EntityType) { ["Id"] = WideKey, ["Code"] = (sbyte)37 };
        for (var field = 0; field < Fields; field++)
        {
            record[FieldName(field)] = Data(field, ChildLength);
        }

        Assert.True((await set.InsertAsync(record).AsTask().WaitAsync(Bound)).Changed);
        await InsertAsync(master, 1, AnchorFill);
        await child.WaitForPositionAsync(master.Position, Bound);
        var original = Inspect(node);
        Assert.Equal(1, allocator.Outstanding);
        Assert.Equal(RegionsPerSegment, SegmentAt(original.Arena, original.Root.Segment).RegionsInUse);
        Assert.All(original.Children, address => Assert.Equal(original.Root.Segment, address.Segment));

        using (var setupPause = new ViewerPause())
        {
            var oldView = HoldWide(wide, 37, setupPause);
            try
            {
                await setupPause.EnteredAsync(oldView);
                var update = child.Set("Wide").CreateUpdate(WideKey);
                update.Assign("Code", WrittenCode);
                Assert.True((await update.CommitAsync().AsTask().WaitAsync(Bound)).Changed);
                await child.WaitForPositionAsync(master.Position, Bound);
                await InsertAsync(master, sourceFill, RegionsPerSegment - 1);
                await InsertAsync(master, reserveKey, 1);
                await child.WaitForPositionAsync(master.Position, Bound);
                Assert.Equal(3, allocator.Outstanding);
                Assert.Equal(BlockKindEnum.Root, original.Arena.Header(original.Root).Kind);
                Assert.Equal(RegionBytes, node.Counters.Arena.RetiredBytes);
            }
            finally
            {
                setupPause.Release();
                await oldView.WaitAsync(Bound);
            }
        }

        var before = Inspect(node);
        var full = Locations(node);
        var sourceWitness = full[sourceFill];
        Assert.Equal(sourceWitness.Chunk.Segment, before.Root.Segment);
        Assert.NotEqual(original.Root.Segment, before.Root.Segment);
        Assert.Equal(original.Children, before.Children);
        var sourceNative = NativeOf(allocator, sourceWitness);
        var reserveNative = NativeOf(allocator, full[reserveKey]);

        await DeleteAsync(master, Enumerable.Range(sourceFill, RegionsPerSegment - 1).Append(reserveKey));
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();
        Assert.Equal(1, SegmentAt(before.Arena, before.Root.Segment).RegionsInUse);
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.Throws<ArgumentException>("address", () => _ = before.Arena.Header(original.Root));
        Assert.True(wide.TryGet(WideKey, out var copy, out var revision));
        ExpectCopy(copy);
        var position = node.Position;
        var requests = allocator.Requests;
        var returns = allocator.Returned;
        using var pause = new ViewerPause();
        var held = HoldWide(wide, WrittenCode, pause);
        try
        {
            await pause.EnteredAsync(held);
            await RunTurnsAsync(node, TurnsPerSweep);
            var current = Inspect(node);
            Assert.True(before.Root != current.Root,
                $"The {Fields}-child root stayed at {before.Root} despite free region {original.Root}.");
            Assert.Equal(original.Root, current.Root);
            Assert.Equal(before.Children, current.Children);
            Assert.Equal(before.Handle, current.Handle);
            Assert.Equal(RegionBytes, node.Counters.Arena.RetiredBytes);
            Assert.Contains(sourceNative, Outstanding(allocator));
            Assert.Equal(returns, allocator.Returned);
            Assert.Equal(before.Handle, before.Arena.Header(before.Root).Root);
            Assert.Equal(position, node.Position);
            Assert.True(wide.TryGet(WideKey, out var next, out var nextRevision));
            Assert.Equal(revision, nextRevision);
            ExpectCopy(next);
        }
        finally
        {
            pause.Release();
            await held.WaitAsync(Bound);
        }

        Assert.Equal(revision, await held.WaitAsync(Bound));
        await RunTurnsAsync(node, TurnsPerSweep);
        var move = Assert.Single(samples, sample => sample.SlotBytes > 0);
        Assert.Equal(BlockKindEnum.Root, move.SourceKind);
        Assert.Equal(RegionBytes, move.SlotBytes);
        Assert.Equal(SegmentBytes, samples.Sum(sample => sample.ReturnedNativeBytes));
        Assert.Equal(returns + 1, allocator.Returned);
        Assert.Equal(requests, allocator.Requests);
        Assert.DoesNotContain(sourceNative, Outstanding(allocator));
        Assert.Contains(reserveNative, Outstanding(allocator));
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(position, node.Position);
        ExpectCopy(copy);
        OwnershipAudit.Expect(node);
    }

    private static WideLocation Inspect(EngineDatabase node)
    {
        WideLocation? located = null;
        node.ForEachLiveRoot((arena, layout, handle, root) =>
        {
            if (layout.EntityType.Name != "Wide")
            {
                return;
            }

            var payload = arena.Payload(root);
            Assert.Equal(RegionBytes, Arena.BlockSizeFor(payload.Length));
            var children = new ArenaAddress[Fields];
            for (var field = 0; field < Fields; field++)
            {
                var offset = layout.StoredFieldOf(layout.EntityType.GetField(FieldName(field))).Offset;
                var descriptor = ResidentDescriptor.Read(payload[(offset - BlockHeader.Size)..]);
                Assert.Equal(DescriptorKindEnum.Block, descriptor.Kind);
                Assert.Equal(ChildLength, (int)descriptor.Length);
                var child = ValueBlocks.AddressOf(descriptor);
                var header = arena.Header(child);
                Assert.Equal((handle, root, (uint)offset, BlockKindEnum.Value),
                    (header.Root, header.Parent, header.DescriptorOffset, header.Kind));
                Assert.Equal(ChildSlot, Arena.BlockSizeFor((int)header.PayloadLength));
                Assert.True(arena.Payload(child).SequenceEqual(Data(field, ChildLength)));
                children[field] = child;
            }

            Assert.Equal(Fields, children.Distinct().Count());
            located = new WideLocation(arena, root, handle, children);
        });
        return Assert.IsType<WideLocation>(located);
    }

    private static Task<LatticeRevision> HoldWide(ILatticeSet items, sbyte code, ViewerPause pause) =>
        OwnThread.Run(() =>
        {
            Assert.True(items.TryView(WideKey, view =>
            {
                var borrowed = view[FieldName(Fields - 1)].GetPart(0);
                pause.Hold();
                Assert.Equal(code, view["Code"].Get<sbyte>());
                Assert.True(borrowed.SequenceEqual(Data(Fields - 1, ChildLength)));
                return view.Revision;
            }, out var revision));
            return revision;
        });

    private static void ExpectCopy(LatticeRecord copy)
    {
        Assert.Equal(WrittenCode, copy.Get<sbyte>("Code"));
        for (var field = 0; field < Fields; field++)
        {
            Assert.Equal(Data(field, ChildLength), copy.Get<byte[]>(FieldName(field)));
        }
    }

    private static string FieldName(int field) => $"Field{field}";

    private static LatticeSchema MakeSchema()
    {
        var children = Enumerable.Range(0, Fields).Select(field =>
            $$""",{"Id":{{field + 3}},"Name":"{{FieldName(field)}}","Type":"Binary"}""");
        return LatticeSchema.Compile($$"""
            {"StringEncoding":"Utf8","EntityTypes":[
              {"Id":1,"Name":"Item","Fields":[
                {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Code","Type":"Int8"},
                {"Id":3,"Name":"Data","Type":"Binary"}],"PrimaryKey":[1]},
              {"Id":2,"Name":"Wide","Fields":[
                {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Code","Type":"Int8"}{{string.Concat(children)}}],
                "PrimaryKey":[1]}]}
            """);
    }

    private sealed record WideLocation(Arena Arena, ArenaAddress Root, RootHandle Handle, ArenaAddress[] Children);
}
