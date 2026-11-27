using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes.Primary;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Measurements;
using Moltaro.Lattice.Measurements.Scenarios.StorageChurn;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Budgeting;
using Moltaro.Lattice.TestSupport.Replication;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Inventory;

// The charge is never below what the engine holds and never above it by more than the headroom and
// the estimates in flight (HeldInventory): exactly at every point of rest, after a load, during a
// growth that a reader holds, after refusals and cancellations and after the churn trace.
public sealed class HeldInventoryTests
{
    private const long Limit = 64L * Segment;

    // The structures a load of rows with secondary indexes and Dictionary text exercises on a master,
    // and on its child, which applies a replicated replace after its load. The generations of the
    // primary tables the growth of the load retired are freed by the look after its publication, so
    // RetiredMemory holds them only while a reader holds them, which While_a_reader_holds… exercises.
    // The publication scratch grows with the data only through a unique index, which
    // A_batch_through_a_unique_index… exercises.
    private static readonly string[] Loaded =
        ["Arena", "RootTable", "PrimaryIndex", "TextTable", "RetiredQueue"];

    private static readonly string[] Followed =
    [
        "Arena", "RootTable", "PrimaryIndex", "TextTable", "RetiredQueue", "FrameConnection", "ChargedSet", "ReceivedChanges",
        "BlockImport",
    ];

    [Fact]
    public async Task After_a_load_the_charge_is_exactly_what_the_master_and_its_child_hold_and_their_headroom()
    {
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(Limit, masterAllocator, listen: true, schema: IndexSchema.Utf8);
        var batch = master.CreateWriteBatch();
        for (var key = 1; key <= 300; key++)
        {
            batch.Insert(new IndexSchema.Ranked { Id = key, Number = key, Login = $"login {key}", Region = $"region {key}" });
            batch.Insert(new IndexSchema.Counted { Id = key, Number = key % 11 });
        }

        var loaded = await batch.CommitAsync().AsTask().WaitAsync(Bound);
        var held = ExpectAtRest(master, masterAllocator);
        Assert.Equal(Headroom.For(IndexSchema.Utf8, new LatticeDatabaseOptions
        {
            Schema = IndexSchema.Utf8,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            ListenUri = MasterAndChild.Loopback,
            MemoryLimit = Limit,
        }), held.Headroom);
        Assert.True(held.GrantedSegments > 0);
        ExpectOwners(held, Loaded);

        var childAllocator = new CountingNativeAllocator();
        var options = ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, Limit);
        await using var child = await ReplicationNodes.OpenChildAsync(options,
            new EngineTestInputs { Allocator = childAllocator, DisableBackgroundMaintenance = true });
        Assert.Equal(loaded.Position, child.Position);
        var childHeld = ExpectAtRest(child, childAllocator);
        Assert.Equal(Headroom.For(IndexSchema.Utf8, options), childHeld.Headroom);

        var later = await master.Set<IndexSchema.Ranked>().ReplaceAsync(
            new IndexSchema.Ranked { Id = 7, Number = 7, Login = "changed", Region = "a new region" });
        await child.WaitForPositionAsync(later.Position, Bound);
        ExpectOwners(ExpectAtRest(master, masterAllocator), [.. Loaded, "FrameConnection"]);
        ExpectOwners(ExpectAtRest(child, childAllocator), Followed);
        Assert.Equal("a new region", child.Set<IndexSchema.Ranked>().Get(7L)?.Region);
    }

    [Fact]
    public async Task A_batch_through_a_unique_index_is_charged_its_publication_scratch_on_the_master_and_its_child()
    {
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(Limit, masterAllocator, listen: true, schema: UniqueSchema.Schema);
        var seed = master.CreateWriteBatch();
        for (var key = 1L; key <= 64; key++)
        {
            seed.Insert(UniqueSchema.Of(key, $"login {key}"));
        }

        await seed.CommitAsync().AsTask().WaitAsync(Bound);
        var childAllocator = new CountingNativeAllocator();
        await using var child = await ReplicationNodes.OpenChildAsync(
            ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, Limit),
            new EngineTestInputs { Allocator = childAllocator, DisableBackgroundMaintenance = true });

        // Every member changes its unique value: the batch claims the new values and frees the old.
        var renames = master.CreateWriteBatch();
        for (var key = 1L; key <= 64; key++)
        {
            renames.Replace(UniqueSchema.Of(key, $"renamed {key}"));
        }

        var renamed = await renames.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(renamed.Position, Bound);
        ExpectOwners(ExpectAtRest(master, masterAllocator), "ChangePublisher");
        ExpectOwners(ExpectAtRest(child, childAllocator), "ChangePublisher");
        Assert.Equal([64L], UniqueSchema.OwnersOf(child.Set<UniqueSchema.Account>(), "renamed 64"));
    }

    [Fact]
    public async Task A_connected_peer_is_charged_its_frame_buffers_before_it_sends_hello_until_its_connection_ends()
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(Limit, allocator, listen: true);
        await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, 8));
        var before = ExpectAtRest(master, allocator);
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, master.ListenUri!.Port)).WaitAsync(Bound);
            await Eventually.HoldsAsync(() => Buffers(master.InspectHeld()) > Buffers(before), "The master did not charge the buffers of the silent peer.");
            var connected = ExpectAtRest(master, allocator);
            Assert.InRange(connected.Charged - before.Charged, 192 * 1024, long.MaxValue);
        }

        await Eventually.HoldsAsync(() => Buffers(master.InspectHeld()) == Buffers(before), "The closed peer still holds its buffers.");
        Assert.Equal(before.Charged, ExpectAtRest(master, allocator).Charged);

        // The receive and send buffers of a connection are arrays of bytes of 64 KiB or more.
        static int Buffers(HeldMemory held) => held.Arrays.Count(array => array.ElementType == typeof(byte) && array.Length >= 64 * 1024);
    }

    [Fact]
    public async Task While_a_reader_holds_the_old_primary_table_both_generations_are_charged_until_it_leaves()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator);
        var set = node.Set<PieceSchema.Piece>();
        var keys = CollidingKeys.Of(9);
        foreach (var key in keys[..8])
        {
            await set.InsertAsync(PieceSchema.Of(key, 8));
        }

        node.DrainRetired();
        var before = ExpectAtRest(node, allocator);
        using var reader = await HeldRead.StartAsync(node, () => set.Get(keys[0])?.Id);
        var grew = false;
        node.ArmGrowthSwitch(set.EntityType, () => grew = true);
        await set.InsertAsync(PieceSchema.Of(keys[8], 8));
        Assert.True(grew);

        var both = ExpectAtRest(node, allocator);
        ExpectOwners(both, "RetiredMemory");
        Assert.True(node.Counters.RetiredIndexBytes > 0);
        Assert.True(Arrays(both) > Arrays(before));

        Assert.Equal(keys[0], await reader.ReleaseAsync());
        node.DrainRetired();
        var released = ExpectAtRest(node, allocator);
        Assert.Equal(0, node.Counters.RetiredIndexBytes);
        Assert.True(released.Charged < both.Charged);
        Assert.Equal(keys.Order(), set.Enumerate().Select(piece => piece.Id).Order());
    }

    [Fact]
    public async Task After_refusals_and_cancellations_the_charge_is_exactly_what_remains_held()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        await using var node = await OpenAsync(4L * Segment, allocator, persistence: storage);
        var pieces = node.Set<PieceSchema.Piece>();

        // The stored piece leaves fewer than four regions of its segment free (see the refused batch below).
        await pieces.InsertAsync(PieceSchema.Of(1, 8, data: Segment - 4 * Arena.MaxBlockSize));
        ExpectAtRest(node, allocator);

        await RefusedAsync(pieces.InsertAsync(PieceSchema.Of(2, 8, data: 14 * 1024 * 1024)).AsTask(), LatticeLimitEnum.Memory);
        ExpectAtRest(node, allocator);

        var refused = node.CreateWriteBatch();
        // The refused write left the segment it obtained in the reserve, so two segments are charged. The
        // estimates of the three members, half a segment each, 1.5 segments together, fit in the two
        // segments the limit of four leaves beside them. Their full chunks of ChunkPayload bytes take 32
        // regions of one block each a member, 96 together, more than the fewer than four regions the
        // stored piece leaves free in the first segment and the 64 of the reserve hold, so the members need
        // a third segment, which the limit cannot give beside their estimates.
        refused.Insert(PieceSchema.Of(3, 8, data: Segment / 2));
        refused.Insert(PieceSchema.Of(4, 8, data: Segment / 2));
        refused.Insert(PieceSchema.Of(5, 8, data: Segment / 2));
        await RefusedAsync(refused.CommitAsync().AsTask(), LatticeLimitEnum.Memory);
        ExpectAtRest(node, allocator);

        var deletes = node.CreateWriteBatch();
        for (var key = 100L; key < 132; key++)
        {
            deletes.Delete<PieceSchema.Piece>(key);
        }

        ExpectInFlight(node, allocator, BuilderEstimate + 32 * BatchInputCharge.PerMemberBytes);
        await deletes.CommitAsync().AsTask().WaitAsync(Bound);
        ExpectAtRest(node, allocator);

        storage.Hold();
        try
        {
            var blocker = pieces.DeleteAsync(-1L).AsTask();
            await storage.NextAsync();
            using var stop = new CancellationTokenSource();
            var cancelled = pieces.InsertAsync(PieceSchema.Of(6, 8, data: 256 * 1024), stop.Token).AsTask();
            await CountersWait.UntilAsync(node, counters => counters.QueuedWrites == 2, "the later write is prepared behind the commit");
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
            storage.ReleaseAll();
            await blocker.WaitAsync(Bound);
        }
        finally
        {
            storage.ReleaseAll();
        }

        ExpectAtRest(node, allocator);
        Assert.Equal(new[] { 1L }, pieces.Enumerate().Select(piece => piece.Id));
    }

    [Fact]
    public async Task After_the_churn_trace_the_charge_is_exactly_what_the_master_holds()
    {
        var schema = LatticeSchema.Compile(ReferenceFixture.SchemaJson);
        var counts = new Dictionary<string, int>();
        var seed = ReferenceFixture.Generate(schema).Where(record =>
        {
            var type = record.EntityType.Name;
            var most = type == "User" ? ChurnTrace.UsersInShortRuns : 128;
            if (counts.GetValueOrDefault(type) == most)
            {
                return false;
            }

            counts[type] = counts.GetValueOrDefault(type) + 1;
            return true;
        }).ToList();
        var trace = new ChurnTrace(smoke: false, ChurnTrace.UsersInShortRuns, cyclic: true, cyclicSmoke: true);
        var audit = new ChurnAudit(seed);
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator, schema: schema);
        foreach (var record in seed)
        {
            await node.Set(record.EntityType.Name).InsertAsync(record);
        }

        ExpectAtRest(node, allocator);
        var users = node.Set("User");
        foreach (var frame in trace.Frames)
        {
            var row = ChurnTrace.User(users.EntityType, frame.Key, frame.Number + 100, frame.Size, frame.Region);
            if (frame.Kind == ChurnTrace.ChangeEnum.Exchange)
            {
                var exchange = node.CreateWriteBatch();
                exchange.Delete("User", frame.OldKey);
                exchange.Insert(row);
                await exchange.CommitAsync().AsTask().WaitAsync(Bound);
            }
            else
            {
                await users.ReplaceAsync(row).AsTask().WaitAsync(Bound);
            }

            if (frame.Collection is ChurnTrace.CollectionChangeEnum.Grow or ChurnTrace.CollectionChangeEnum.Trim)
            {
                await audit.EditCollectionsAsync(node, frame, trace.CollectionExtra, CancellationToken.None).AsTask().WaitAsync(Bound);
            }
        }

        ExpectAtRest(node, allocator);
        node.DrainRetired();
        ExpectAtRest(node, allocator);
    }

    [Fact]
    public async Task Before_close_the_charge_is_exactly_what_is_held_and_after_close_every_native_grant_is_returned()
    {
        var allocator = new CountingNativeAllocator();
        await using (var node = await OpenAsync(Limit, allocator, schema: IndexSchema.Utf8))
        {
            var ranked = node.Set<IndexSchema.Ranked>();
            for (var key = 1; key <= 64; key++)
            {
                await ranked.InsertAsync(new IndexSchema.Ranked { Id = key, Number = key, Login = $"login {key}", Region = $"region {key}" });
            }

            await ranked.DeleteAsync(3L);
            node.DrainRetired();
            Assert.True(allocator.OutstandingBytes > 0);
            ExpectAtRest(node, allocator);
            await node.CloseAsync().AsTask().WaitAsync(Bound);
        }

        allocator.ExpectEverythingReturned();
    }
}
