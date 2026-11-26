using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.SegmentEvacuationTests;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

public sealed class SegmentEvacuationResumptionTests
{
    private static readonly LatticeSchema PaddedSchema = LatticeSchema.Compile("""
        {"StringEncoding":"Utf8","EntityTypes":[
          {"Id":1,"Name":"Item","Fields":[
            {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Code","Type":"Int8"},
            {"Id":3,"Name":"Data","Type":"Binary"}],"PrimaryKey":[1]},
          {"Id":2,"Name":"DirectoryPad","Fields":[{"Id":1,"Name":"Id","Type":"Int64"}],"PrimaryKey":[1]},
          {"Id":3,"Name":"RootPad","Fields":[{"Id":1,"Name":"Id","Type":"Int64"},
            {"Id":2,"Name":"Pad","Type":"Guid"}],"PrimaryKey":[1]}]}
        """);

    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task A_bounded_turn_takes_the_only_external_capacity_or_rechecks_it_after_a_forwarded_write(LatticeRoleEnum role)
    {
        const int activeSourceSegments = 5;
        const int reserveKey = TargetKey + activeSourceSegments * RegionsPerSegment;
        const int lastSinkKey = reserveKey - 1;
        const int writerKey = reserveKey + 1;
        const int extent = activeSourceSegments + 2;
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        await using var master = await ReplicationNodes.OpenMasterAsync(PaddedSchema,
            role == LatticeRoleEnum.Master ? Inputs(extent, allocator, samples.Add) : Inputs(extent));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            role == LatticeRoleEnum.Child ? Inputs(extent, allocator, samples.Add) : Inputs(extent));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var items = node.Set("Item");

        await PadAsync(master, "DirectoryPad", 700);
        await PadAsync(master, "RootPad", 550);
        await InsertAsync(master, 1, AnchorChunks);
        for (var group = 0; group < activeSourceSegments; group++)
        {
            await InsertAsync(master, TargetKey + group * RegionsPerSegment, RegionsPerSegment);
        }

        await InsertAsync(master, reserveKey, 1);
        await child.WaitForPositionAsync(master.Position, Bound);
        var full = Locations(node);
        var source = full[TargetKey];
        var hole = full[lastSinkKey].Chunk;
        Assert.True(hole.Segment > source.Chunk.Segment);
        Assert.Equal(extent, allocator.Outstanding);
        await DeleteAsync(master, [lastSinkKey, reserveKey]);
        await child.WaitForPositionAsync(master.Position, Bound);
        node.DrainRetired();
        Assert.Equal(1, node.Counters.Arena.FreeRegions);
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        var anchor = SegmentAt(source.Arena, full[1].Chunk.Segment);
        Assert.All(anchor.Regions, region => Assert.True(region.Live * 2 >= RegionBytes /
            SizeClasses.SizeOf(region.SizeClass)));
        var requests = allocator.Requests;
        var returns = allocator.Returned;
        var expected = Data(TargetKey, ValueBlocks.ChunkPayload);
        var revision = ExpectRecord(items, expected);
        using var pause = new ViewerPause();
        var held = OwnThread.Run(() =>
        {
            Assert.True(items.TryView((long)TargetKey, view =>
            {
                var part = view["Data"].GetPart(0);
                pause.Hold();
                Assert.True(part.SequenceEqual(expected));
                return view.Revision;
            }, out var value));
            return value;
        });
        var published = false;
        try
        {
            await pause.EnteredAsync(held);
            // Published charge falls, but these retired slots stay occupied while the view is held.
            await DeleteAsync(master, Enumerable.Range(TargetKey + 1, RegionsPerSegment - 1));
            await child.WaitForPositionAsync(master.Position, Bound);
            Assert.Equal(1, node.Counters.Arena.FreeRegions);
            var sourceRegions = SegmentAt(source.Arena, source.Chunk.Segment).Regions;
            Assert.Equal(1, sourceRegions.Sum(region => region.Live));
            Assert.Equal(RegionsPerSegment, sourceRegions.Sum(region => region.Allocated));
            Assert.Throws<ArgumentException>("address", () => _ = source.Arena.Header(hole));

            var position = node.Position;
            MaintenanceTurnSample offered = default;
            for (var turn = 0; turn < TurnsPerSweep; turn++)
            {
                await RunTurnsAsync(node, 1);
                offered = samples.Last(sample => sample.Admitted);
                Assert.InRange(offered.SelectionWork, 0, AutomaticMaintenance.SelectionWorkLimit);
                if (offered.CandidatesExamined != 0 || offered.SlotBytes != 0)
                {
                    break;
                }
            }

            Assert.True(offered.CandidatesExamined != 0 || offered.SlotBytes != 0,
                "The only eligible 64 KiB source was neither examined nor moved in the bounded sweep.");
            published = offered.SlotBytes != 0;
            if (published)
            {
                Assert.Equal(source.Chunk, offered.Source);
                Assert.Equal(hole, offered.Destination);
                Assert.Equal(RegionBytes, offered.SlotBytes);
                Assert.Equal(hole, Locations(node)[TargetKey].Chunk);
                Assert.Equal(0, node.Counters.Arena.FreeRegions);
                Assert.True(source.Arena.Payload(source.Chunk).SequenceEqual(expected));
            }
            else
            {
                await InsertAsync(child, writerKey, 1);
                await child.WaitForPositionAsync(master.Position, Bound);
                Assert.Equal(hole, Locations(node)[writerKey].Chunk);
                Assert.Equal(0, node.Counters.Arena.FreeRegions);
                Assert.Equal(1, sourceRegions.Sum(region => region.Live));
                position = node.Position;
                await RunTurnsAsync(node, TurnsPerSweep);
                Assert.Equal(source, Locations(node)[TargetKey]);
                Assert.All(samples, sample => Assert.Equal(0, sample.SlotBytes));
            }

            Assert.Equal(position, node.Position);
            Assert.Equal(revision, ExpectRecord(items, expected));
            Assert.Equal(requests, allocator.Requests);
            Assert.Equal(returns, allocator.Returned);
        }
        finally
        {
            pause.Release();
            await held.WaitAsync(Bound);
        }

        if (!published)
        {
            await DeleteAsync(master, [writerKey]);
            await child.WaitForPositionAsync(master.Position, Bound);
        }

        var finalPosition = node.Position;
        await RunTurnsAsync(node, TurnsPerSweep);
        Assert.All(samples, sample => Assert.InRange(sample.SelectionWork, 0, AutomaticMaintenance.SelectionWorkLimit));
        Assert.Equal(finalPosition, node.Position);
        Assert.Equal(hole, Locations(node)[TargetKey].Chunk);
        Assert.Equal(returns + 1, allocator.Returned);
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(revision, ExpectRecord(items, expected));
    }

    private static async Task PadAsync(EngineDatabase database, string type, int count)
    {
        var set = database.Set(type);
        var batch = database.CreateWriteBatch();
        for (var id = 0; id < count; id++)
        {
            var value = new LatticeRecord(set.EntityType) { ["Id"] = (long)id };
            if (type == "RootPad")
            {
                value["Pad"] = Guid.Empty;
            }

            batch.Insert(value);
        }

        Assert.True((await batch.CommitAsync().AsTask().WaitAsync(Bound)).Changed);
    }
}
