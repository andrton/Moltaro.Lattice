using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Allocation;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Constants;

// The headroom covers the managed memory an open instance keeps beside the arrays its structures
// report and its segment objects: measured as what the heap retains once the instance is open, after
// a first instance of the same form has been opened and closed so that the caches of the process
// are made. A master without and with a listener and a child are measured; the parts of the
// headroom each one adds have to cover what it retains. What reads may keep of the text table
// (Headroom.ReplacedTextBytes) is no part of what an open instance retains, so it is left out of the
// comparison. On a schema without a text table it is zero, and the measured parts alone are stated:
// one entity type, for the fixed parts of an instance, a listener and a child; many entity types, for
// the part of each type with its primary index; and one type with many secondary indexes, for the part
// of each index. A schema with texts keeps the fixed objects of the text table covered.
//
// The heap of the process changes beside the instance, and the measurement leaves those changes out.
// A thread that allocates takes an allocation context of 8 KiB, which GC.GetTotalMemory counts as in
// use until the next collection: the threads that finish the work of an open or a close right after it
// returns, and the work of other tests. And earlier tests let go of what they held. So each window
// runs from a still heap, two readings in a row that agree, to a still heap with the instance open,
// and each figure is the median of MeasuredOpens windows: what other work does falls into some of
// them, what the instance retains is in every one.
[Collection<WholeProcessAllocationCollection>]
public sealed class HeadroomTests
{
    private const long Limit = 64L * Segment;

    private const int MeasuredOpens = 5;

    // Schemas without a text table: one entity type, as many entity types as a child of the parts of each
    // type shows above the fixed parts, each with its key and one Int64 field and no index, and one entity
    // type with as many Int64 fields, each in a secondary index of its own.
    private static readonly LatticeSchema OneType = Types(1);

    private static readonly LatticeSchema ManyTypes = Types(32);

    private static readonly LatticeSchema ManyIndexes = Indexes(32);

    [Fact]
    public async Task Where_the_schema_has_no_text_table_the_headroom_covers_what_a_master_a_listener_and_a_child_retain()
    {
        Assert.Equal(0, Headroom.ReplacedTextBytes(OneType));
        Assert.Equal(0, Headroom.ReplacedTextBytes(ManyTypes));
        Assert.Equal(0, Headroom.ReplacedTextBytes(ManyIndexes));

        await ExpectMasterCoveredAsync(OneType, listen: false);
        await ExpectMasterCoveredAsync(OneType, listen: true);
        await ExpectChildCoveredAsync(OneType);
        await ExpectChildCoveredAsync(ManyTypes);
        await ExpectChildCoveredAsync(ManyIndexes);
    }

    [Fact]
    public async Task The_headroom_covers_what_an_open_master_and_its_child_retain_beside_their_arrays()
    {
        await ExpectMasterCoveredAsync(IndexSchema.Utf8, listen: false);
        await ExpectMasterCoveredAsync(IndexSchema.Utf8, listen: true);
        await ExpectChildCoveredAsync(IndexSchema.Utf8);
    }

    private static async Task ExpectChildCoveredAsync(LatticeSchema schema)
    {
        await using var master = await OpenAsync(Limit, new CountingNativeAllocator(), listen: true, schema: schema);
        await (await OpenChildAsync(master)).DisposeAsync();
        var besides = new List<long>();
        var headroom = 0L;
        for (var open = 0; open < MeasuredOpens; open++)
        {
            // The master ends the connection of a closed child when it reads its end, on its own task.
            await Eventually.HoldsAsync(() => master.InspectHeld().Arrays.All(array => array.Owner != "FrameConnection"),
                "The master still holds the buffers of the connection of the closed child.");
            var masterBefore = master.InspectHeld();
            var heap = await StillAsync();
            var child = await OpenChildAsync(master);
            try
            {
                var retained = await StillAsync() - heap;
                var held = child.InspectHeld();
                var masterAfter = master.InspectHeld();
                var arrays = Arrays(held) + Arrays(masterAfter) - Arrays(masterBefore);
                var segments = (held.GrantedSegments + masterAfter.GrantedSegments - masterBefore.GrantedSegments) * ObjectBytes(typeof(ArenaSegment));

                // The blocks of the delivery tail of the master are outside admission and not among the arrays.
                var delivery = masterAfter.DeliveryCapacityBytes - masterBefore.DeliveryCapacityBytes;
                besides.Add(retained - arrays - segments - delivery);
                headroom = held.Headroom;
            }
            finally
            {
                await child.DisposeAsync();
            }
        }

        ExpectCovered("child", schema, Median(besides), headroom);
    }

    private static async Task ExpectMasterCoveredAsync(LatticeSchema schema, bool listen)
    {
        await (await OpenAsync(Limit, new CountingNativeAllocator(), listen: listen, schema: schema)).DisposeAsync();
        var besides = new List<long>();
        var headroom = 0L;
        for (var open = 0; open < MeasuredOpens; open++)
        {
            var heap = await StillAsync();
            var node = await OpenAsync(Limit, new CountingNativeAllocator(), listen: listen, schema: schema);
            try
            {
                var retained = await StillAsync() - heap;
                var held = node.InspectHeld();
                besides.Add(retained - Arrays(held) - held.GrantedSegments * ObjectBytes(typeof(ArenaSegment)));
                headroom = held.Headroom;
            }
            finally
            {
                await node.DisposeAsync();
            }
        }

        ExpectCovered(listen ? "master that listens" : "master", schema, Median(besides), headroom);
    }

    // What the instance retains beside its arrays is within its headroom without what reads may keep of the
    // text table.
    private static void ExpectCovered(string node, LatticeSchema schema, long beside, long headroom)
    {
        var covered = headroom - Headroom.ReplacedTextBytes(schema);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A {node} ({schema.EntityTypes.Count} entity types, {schema.EntityTypes.Sum(type => type.Indexes.Count)} secondary indexes) retains {beside} bytes beside its arrays; its headroom without the text term is {covered}.");
        Assert.True(beside <= covered, $"A {node} retains {beside} bytes beside its arrays; its headroom without the text term is {covered}.");
    }

    // A schema of one entity type with its key and the given number of Int64 fields, each in a secondary
    // index of its own.
    private static LatticeSchema Indexes(int count)
    {
        var fields = Enumerable.Range(2, count).Select(field => $"{{ \"Id\": {field}, \"Name\": \"Value{field}\", \"Type\": \"Int64\" }}");
        var indexes = Enumerable.Range(2, count).Select(field => $"{{ \"Id\": {field - 1}, \"Name\": \"ByValue{field}\", \"Fields\": [{field}] }}");
        return LatticeSchema.Compile($"{{ \"EntityTypes\": [ {{ \"Id\": 1, \"Name\": \"Indexed\", \"Fields\": [ {{ \"Id\": 1, \"Name\": \"Id\", \"Type\": \"Int64\" }}, " +
            $"{string.Join(", ", fields)} ], \"PrimaryKey\": [1], \"Indexes\": [ {string.Join(", ", indexes)} ] }} ] }}");
    }

    // A schema of the given number of entity types, each with its key and one Int64 field.
    private static LatticeSchema Types(int count)
    {
        var types = Enumerable.Range(1, count).Select(type =>
            $"{{ \"Id\": {type}, \"Name\": \"Type{type}\", \"Fields\": [ {{ \"Id\": 1, \"Name\": \"Id\", \"Type\": \"Int64\" }}, " +
            $"{{ \"Id\": 2, \"Name\": \"Value\", \"Type\": \"Int64\" }} ], \"PrimaryKey\": [1] }}");
        return LatticeSchema.Compile($"{{ \"EntityTypes\": [ {string.Join(", ", types)} ] }}");
    }

    // The heap once two readings in a row agree.
    private static async Task<long> StillAsync()
    {
        var last = Retained();
        var still = last;
        await Eventually.HoldsAsync(() =>
        {
            still = Retained();
            var agrees = still == last;
            last = still;
            return agrees;
        }, "The heap of the process did not keep still.");
        return still;
    }

    private static long Median(List<long> figures) => figures.Order().ElementAt(figures.Count / 2);

    private static Task<EngineDatabase> OpenChildAsync(EngineDatabase master) =>
        ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, Limit),
            new EngineTestInputs { Allocator = new CountingNativeAllocator(), DisableBackgroundMaintenance = true });

    private static long Retained()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}
