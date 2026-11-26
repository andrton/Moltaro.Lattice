using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Views;
using Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.TestSupport.Concurrency;
using static Moltaro.Lattice.Engine.Tests.Engine.Collections.CollectionShrinkPair;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

// Under Q14 and Q17, safely reclaimable spare capacity cannot turn a fitting
// logical write into a refusal. Reader retirement and unpublished writes retain
// their prior lifetime guarantees. Each grant here is one actual native segment.
public sealed class CollectionPressureTests
{
    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int32" },
                { "Id": 2, "Name": "Values", "Type": "Map", "Key": { "Type": "Int32" }, "Value": { "Type": "Int32" } }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Blob",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int32" },
                { "Id": 2, "Name": "Bytes", "Type": "Binary" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Theory]
    [InlineData(9_000, 5, 7, 180_000, 2, 3, 1, WholeChunk)]
    [InlineData(17_100, 5_731, 11, 300_000, 3, 6, 2, WholeChunk)]
    [InlineData(6_000, 5, 8, 240_000, 2, 5, 2, 3_328)]
    public async Task A_fitting_write_can_return_ready_lookup_grants_without_waiting_for_shrink(
        int full, int small, int nativeLimit, int binarySize, int readyGrants, int newGrants, int minimumReturns, int smallestReadySlot)
    {
        var allocator = new CountingNativeAllocator();
        var time = new CollectionMaintenanceTime();
        await using var database = await OpenAsync(nativeLimit, allocator, time);
        var node = new CollectionShrinkNode(database, allocator);
        var items = database.Set("Item");
        await InsertMapAsync(items, full);
        var original = node.LookupOf(Key, flattened: false);
        Assert.Equal(readyGrants, original.Chunks.Length);
        Assert.Equal(smallestReadySlot, original.Chunks.Min(slot => slot.NativeBytes));
        Assert.Equal(WholeChunk, original.Chunks.Max(slot => slot.NativeBytes));

        var reduced = await ResizeAsync(items, full, small);
        node.Turn(12);
        var current = node.LookupOf(Key, flattened: false);
        var ready = ExpectReady(node, original);
        node.ExpectNativeLedger();
        var revision = ExpectMap(items, small);
        var capacity = current.Active + ready.Sum(entry => entry.Slot.PayloadBytes / sizeof(ulong));
        Assert.Equal(small == Small ? 8 : 8188, current.Active);
        if (small == Small)
        {
            Assert.True(capacity > 4 * Need(small));
            time.Advance(TimeSpan.FromMilliseconds(4_999));
        }
        else
        {
            Assert.Equal(4 * Need(small), capacity);
            time.Advance(TimeSpan.FromSeconds(5));
            node.Turn(12);
            ExpectReady(node, original);
        }

        var oldObtained = allocator.Obtained;
        var oldReturned = allocator.Returned;
        var oldOutstanding = allocator.Outstanding;
        Assert.InRange(oldOutstanding + newGrants - readyGrants, 1, nativeLimit);
        Assert.True(oldOutstanding + newGrants > nativeLimit);
        var blobs = database.Set("Blob");
        var bytes = Binary(binarySize);

        // No maintenance call between the pressure cut and the ordinary write.
        var inserted = await InsertBinaryAsync(blobs, bytes);
        Assert.True(inserted.Changed);
        Assert.Equal(reduced.Position.Sequence + 1, inserted.Position.Sequence);
        Assert.Equal(inserted.Position, database.Position);
        Assert.Equal(bytes, blobs.Get(2)!.Get<byte[]>("Bytes"));
        Assert.Equal(revision, ExpectMap(items, small));
        Assert.Equal(current.Root, node.LookupOf(Key, flattened: false).Root);
        Assert.Equal(newGrants, allocator.Obtained - oldObtained);
        var returned = allocator.Returned - oldReturned;
        Assert.InRange(returned, minimumReturns, readyGrants);
        Assert.Equal(returned, node.Observe(original.Chunks).Zip(original.Chunks)
            .Count(slot => !slot.First.SameGrantAs(slot.Second)));
        Assert.Equal((long)WholeChunk * (oldOutstanding + newGrants - returned),
            (long)WholeChunk * allocator.Outstanding);
        Assert.Equal(readyGrants - returned, node.LeaseSnapshot().Length);
        Assert.Equal(0, database.Counters.Arena.RetiredBytes);
        Assert.Equal(0, database.Counters.Arena.ReservedSegments);
        node.ExpectNativeLedger();
    }

    [Fact]
    public async Task A_fitting_write_cannot_take_lookup_grants_from_a_reader_still_using_them()
    {
        var allocator = new CountingNativeAllocator();
        var time = new CollectionMaintenanceTime();
        await using var database = await OpenAsync(7, allocator, time);
        var node = new CollectionShrinkNode(database, allocator);
        var items = database.Set("Item");
        await InsertMapAsync(items, Full);
        var original = node.LookupOf(Key, flattened: false);
        var originalRevision = ExpectMap(items, Full);
        using var pause = new ViewerPause();
        var reading = OwnThread.Run(() =>
        {
            Assert.True(items.TryView(Key, view =>
            {
                Assert.Equal(originalRevision, view.Revision);
                ExpectMap(view["Values"].Map, Full);
                pause.Hold();
                ExpectMap(view["Values"].Map, Full);
                return view.Revision;
            }, out var revision));
            return revision;
        });
        await pause.EnteredAsync(reading);

        var reduced = await ResizeAsync(items, Full, Small);
        var revision = ExpectMap(items, Small);
        time.Advance(TimeSpan.FromMilliseconds(4_999));
        var blobs = database.Set("Blob");
        var bytes = Binary(180_000);
        var waiting = RefusedWriteWait.Begins(database);
        using var cancel = new CancellationTokenSource();
        var writing = OwnThread.Run(() => OwnThread.Wait(blobs.InsertAsync(BinaryRecord(blobs, bytes), cancel.Token)));
        await waiting.WaitAsync(Bound);
        Assert.False(writing.IsCompleted);
        Assert.Equal(reduced.Position, database.Position);
        Assert.Null(blobs.Get(2));
        Assert.Equal(revision, ExpectMap(items, Small));
        Assert.Empty(node.LeaseSnapshot());
        Assert.All(node.Observe(original.Chunks).Zip(original.Chunks), slot =>
        {
            Assert.True(slot.First.SameGrantAs(slot.Second));
            Assert.True(slot.First.Retired);
        });
        node.ExpectNativeLedger(heldRoots: [original]);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(Bound));
        pause.Release();
        Assert.Equal(originalRevision, await reading.WaitAsync(Bound));
        node.Turn(12);
        ExpectReady(node, original);
        time.Advance(TimeSpan.FromSeconds(5));
        node.Turn(12);
        Assert.Empty(node.LeaseSnapshot());
        var retried = await InsertBinaryAsync(blobs, bytes);
        Assert.True(retried.Changed);
        Assert.Equal(reduced.Position.Sequence + 1, retried.Position.Sequence);
        Assert.Equal(bytes, blobs.Get(2)!.Get<byte[]>("Bytes"));
        Assert.Equal(revision, ExpectMap(items, Small));
        node.ExpectNativeLedger();
    }

    [Fact]
    public async Task A_write_under_pressure_cannot_take_lookup_grants_borrowed_by_a_pending_write()
    {
        var allocator = new CountingNativeAllocator();
        var time = new CollectionMaintenanceTime();
        var persistence = new HeldPersistence(holding: false);
        await using var database = await OpenAsync(7, allocator, time, persistence);
        var node = new CollectionShrinkNode(database, allocator);
        var items = database.Set("Item");
        await InsertMapAsync(items, Full);
        var original = node.LookupOf(Key, flattened: false);
        var reduced = await ResizeAsync(items, Full, Small);
        node.Turn(12);
        var revision = ExpectMap(items, Small);
        ExpectReady(node, original);
        var blobs = database.Set("Blob");
        time.Advance(TimeSpan.FromMilliseconds(4_999));

        Task<LatticeWriteResult> growing;
        Task<LatticeWriteResult> competing;
        persistence.Hold();
        try
        {
            growing = ResizeAsync(items, Small, Full);
            _ = await persistence.NextAsync();
            var borrowed = ExpectLeaseState(node, original, borrowed: true);
            Assert.All(borrowed, entry => Assert.False(entry.Slot.Parent.IsNull));
            Assert.Equal(7, allocator.Outstanding);
            var before = (allocator.Obtained, allocator.Returned, database.Counters.Arena.PreparedBytes);

            var waiting = RefusedWriteWait.Begins(database);
            competing = InsertBinaryAsync(blobs, Binary(190_000));
            await waiting.WaitAsync(Bound);
            Assert.False(competing.IsCompleted);
            Assert.Equal(before, (allocator.Obtained, allocator.Returned, database.Counters.Arena.PreparedBytes));
            Assert.Equal(borrowed, ExpectLeaseState(node, original, borrowed: true));
            Assert.Equal(reduced.Position, database.Position);
            Assert.Equal(revision, ExpectMap(items, Small));
            Assert.Null(blobs.Get(2));
        }
        finally
        {
            persistence.ReleaseAll();
        }

        var grown = await growing;
        var refused = await Assert.ThrowsAsync<LatticeException>(() => competing);
        Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
        Assert.Equal(reduced.Position.Sequence + 1, grown.Position.Sequence);
        Assert.Equal(grown.Position, database.Position);
        Assert.NotEqual(revision, ExpectMap(items, Full));
        Assert.Null(blobs.Get(2));
        var published = node.LookupOf(Key, flattened: false);
        Assert.All(original.Chunks, old => Assert.Single(published.Chunks, slot => slot.SameGrantAs(old)));
        Assert.Empty(node.LeaseSnapshot());
        node.ExpectNativeLedger();
    }

    private static async Task<EngineDatabase> OpenAsync(int nativeLimit, CountingNativeAllocator allocator,
        TimeProvider time, HeldPersistence? persistence = null)
    {
        var database = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = Schema,
        }, new EngineTestInputs
        {
            Allocator = allocator,
            Arena = new ArenaOptions { MemoryLimit = nativeLimit * WholeChunk, SegmentSize = WholeChunk, ReservedSegments = 0 },
            DisableBackgroundMaintenance = true,
            IndexSeed = 31,
            MaintenanceTimeProvider = time,
            Persistence = persistence,
        });

        await database.OpenAsync().AsTask().WaitAsync(Bound);
        return database;
    }

    private static async Task InsertMapAsync(ILatticeSet items, int count)
    {
        var values = Enumerable.Range(0, count).ToDictionary(index => index, Value);
        var inserted = await items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = Key, ["Values"] = values })
            .AsTask().WaitAsync(Bound);
        Assert.True(inserted.Changed);
    }

    private static Task<LatticeWriteResult> ResizeAsync(ILatticeSet items, int before, int after)
    {
        var updated = items.CreateUpdate(Key);
        for (var index = Math.Min(before, after); index < Math.Max(before, after); index++)
        {
            _ = before > after ? updated.MapRemove("Values", index) : updated.MapSet("Values", index, Value(index));
        }

        return updated.CommitAsync().AsTask().WaitAsync(Bound);
    }

    private static Task<LatticeWriteResult> InsertBinaryAsync(ILatticeSet blobs, byte[] bytes) =>
        blobs.InsertAsync(BinaryRecord(blobs, bytes)).AsTask().WaitAsync(Bound);

    private static LatticeRecord BinaryRecord(ILatticeSet blobs, byte[] bytes) =>
        new(blobs.EntityType) { ["Id"] = 2, ["Bytes"] = bytes };

    private static byte[] Binary(int size) => Enumerable.Range(0, size).Select(index => unchecked((byte)(13 + 17 * index))).ToArray();

    private static int Value(int index) => 3 * index + 1;

    private static (CollectionSlotOwnership Lease, CollectionNativeSlot Slot)[] ExpectReady(CollectionShrinkNode node,
        CollectionLookupShape original) => ExpectLeaseState(node, original, borrowed: false);

    private static (CollectionSlotOwnership Lease, CollectionNativeSlot Slot)[] ExpectLeaseState(CollectionShrinkNode node,
        CollectionLookupShape original, bool borrowed)
    {
        var leases = node.LeaseSnapshot().OrderBy(entry => entry.Slot.Address.Value).ToArray();
        Assert.Equal(original.Chunks.Length, leases.Length);
        Assert.All(leases, entry =>
        {
            Assert.Equal(borrowed, entry.Lease.Borrowed);
            Assert.Equal(original.Owner, entry.Lease.FormerLookup);
            Assert.Equal(original.Handle, entry.Lease.Owner);
            Assert.True(entry.Slot.Prepared);
            Assert.False(entry.Slot.Retired);
            var matching = Assert.Single(original.Chunks, slot => slot.Address == entry.Slot.Address);
            Assert.True(entry.Slot.SameGrantAs(matching));
        });
        return leases;
    }

    private static LatticeRevision ExpectMap(ILatticeSet items, int count)
    {
        Assert.True(items.TryGet(Key, out var item, out var revision));
        var values = Assert.IsType<Dictionary<int, int>>(item["Values"]);
        Assert.Equal(count, values.Count);
        foreach (var index in Enumerable.Range(0, count))
        {
            Assert.Equal(Value(index), values[index]);
        }

        Assert.True(items.TryView(Key, view =>
        {
            ExpectMap(view["Values"].Map, count);
            return view.Revision;
        }, out var viewed));
        Assert.Equal(revision, viewed);
        return revision;
    }

    private static void ExpectMap(LatticeMapView view, int count)
    {
        Assert.Equal(count, view.Count);
        Assert.False(view.TryGetValue(count, out _));
        foreach (var index in Enumerable.Range(0, count))
        {
            Assert.True(view.TryGetValue(index, out var result));
            Assert.Equal(Value(index), result.Get<int>());
        }
    }
}
