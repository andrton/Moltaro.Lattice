using System.Globalization;
using System.Threading.Channels;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Text;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.Text.TextMetadataTestAccess;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// Deferring optional managed metadata work preserves the independent native FIFO.
public sealed class TextMetadataAllocationRefusalTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task The_worker_keeps_reclaiming_and_recovers_after_migration_cannot_allocate(LatticeRoleEnum role)
    {
        const int Distinct = 96;
        var label = new string('p', 48 * ValueBlocks.ChunkPayload);
        var allocator = new CountingNativeAllocator();
        var blocked = 0;
        var refusing = 1;
        var refusals = 0;
        var previousRefusals = 0;
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = Channel.CreateUnbounded<(MaintenanceTurnSample Sample, int Refusals)>();
        using var worker = new HeldEngineCallback();
        var inputs = new EngineTestInputs
        {
            IndexSeed = 17,
            Allocator = allocator,
            Arena = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = 16 * 1024 * 1024, ReservedSegments = 0 },
            BeforeMaintenanceWait = _ =>
            {
                if (Interlocked.Exchange(ref blocked, 1) == 0)
                {
                    worker.Hold();
                }
            },
            BeforeTextMigrationPageAllocation = () =>
            {
                if (Volatile.Read(ref refusing) != 0)
                {
                    Interlocked.Increment(ref refusals);
                    faulted.TrySetResult();
                    throw new OutOfMemoryException("Text migration destination page was refused.");
                }
            },
            ObserveMaintenanceTurn = sample =>
            {
                var count = Volatile.Read(ref refusals);
                var previous = Interlocked.Exchange(ref previousRefusals, count);
                observed.Writer.TryWrite((sample, count - previous));
            },
        };
        await using var master = await ReplicationNodes.OpenMasterAsync(TextSchema.Utf8,
            role == LatticeRoleEnum.Master ? inputs : new EngineTestInputs { DisableBackgroundMaintenance = true });
        await using var child = role == LatticeRoleEnum.Child ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, inputs) : null;
        var node = child ?? master;
        var items = node.Set<TextSchema.Depot>();
        try
        {
            await worker.ReachedAsync();
            var anchor = await items.InsertAsync(new TextSchema.Depot { Id = 1, Area = "persistent dictionary text" });
            await node.WaitForPositionAsync(anchor.Position);
            var id = TextIds(node, "Depot", "Area", [1L])[1L];
            var fresh = node.Counters.TextTableBytes;

            var insert = node.CreateWriteBatch();
            for (var key = 2; key <= Distinct; key++)
            {
                insert.Insert(new TextSchema.Depot { Id = key, Area = "region-" + key.ToString(CultureInfo.InvariantCulture) });
            }

            var inserted = await insert.CommitAsync();
            await node.WaitForPositionAsync(inserted.Position);
            for (var turn = 0; Maintain(node, 64) != 0; turn++)
            {
                Assert.InRange(turn, 0, Distinct);
            }

            var delete = node.CreateWriteBatch();
            for (var key = 2; key <= Distinct; key++)
            {
                delete.Delete<TextSchema.Depot>((long)key);
            }

            var deleted = await delete.CommitAsync();
            await node.WaitForPositionAsync(deleted.Position);
            node.DrainRetired();
            Assert.Equal(1, node.Counters.TextEntries);
            Assert.Empty(Capacity(node).OldBuckets);
            Assert.True(node.Counters.TextTableBytes > fresh);

            var places = node.Set<TextSchema.Place>();
            var placed = await places.InsertAsync(new TextSchema.Place { Id = 1, Label = label });
            await node.WaitForPositionAsync(placed.Position);
            var records = node.Set("Place");
            using var readerPause = new ViewerPause();
            var read = OwnThread.Run(() =>
            {
                Assert.True(records.TryView(1L, view =>
                {
                    var value = view["Label"];
                    var old = value.GetPart(0);
                    Assert.True(old.IndexOfAnyExcept((byte)'p') < 0);
                    readerPause.Hold();
                    Assert.True(old.IndexOfAnyExcept((byte)'p') < 0);
                    return value.ByteLength;
                }, out var bytes));
                return bytes;
            });
            try
            {
                await readerPause.EnteredAsync(read);
                var removed = await places.DeleteAsync(1L);
                await node.WaitForPositionAsync(removed.Position);
                var retired = node.Counters.Arena.RetiredBytes;
                Assert.True(retired > label.Length);
                worker.Release();
                await faulted.Task.WaitAsync(Bound);
                output.WriteLine($"{role}: injected exception in the real worker with {retired} retired native bytes and a held view.");
                await NextRefusalAsync();
                var first = Entry(node, id);
                Assert.NotNull(first);
                Assert.Equal(TextBucketSetEnum.Old, first.Value.BucketSet);
                var oldCapacity = Capacity(node);
                await NextRefusalAsync();
                Assert.Equal(first, Entry(node, id));
                Assert.Equal(Bytes(oldCapacity.OldBuckets), Bytes(Capacity(node).OldBuckets));
                Assert.Equal(Bytes(oldCapacity.ActiveBuckets), Bytes(Capacity(node).ActiveBuckets));
                Assert.Equal(retired, node.Counters.Arena.RetiredBytes);
                Assert.Equal(LatticeDatabaseStateEnum.Ready, node.State);
                Assert.Equal(removed.Position, node.Position);

                readerPause.Release();
                Assert.Equal(label.Length, await read.WaitAsync(Bound));
                for (var turn = 0; node.Counters.Arena.RetiredBytes != 0; turn++)
                {
                    Assert.InRange(turn, 0, Distinct);
                    await NextRefusalAsync();
                }

                output.WriteLine($"{role}: drained the native FIFO during {Volatile.Read(ref refusals)} injected attempts at position {removed.Position}.");
                Assert.Equal(first, Entry(node, id));
                Assert.Equal(removed.Position, node.Position);
                Assert.Equal("persistent dictionary text", items.Get(1L)?.Area);
                Volatile.Write(ref refusing, 0);
                for (var turn = 0; node.Counters.TextTableBytes != fresh; turn++)
                {
                    Assert.InRange(turn, 0, Distinct);
                    await observed.Reader.ReadAsync().AsTask().WaitAsync(Bound);
                }

                Assert.Empty(Capacity(node).OldBuckets);
                Assert.Equal(TextBucketSetEnum.Active, Entry(node, id)!.Value.BucketSet);
                Assert.Equal(1L, References(node, "persistent dictionary text", "Utf8"));
                Assert.Equal(removed.Position, node.Position);
            }
            finally
            {
                readerPause.Release();
                await read.WaitAsync(Bound);
            }
        }
        finally
        {
            worker.Release();
        }

        await node.CloseAsync().AsTask().WaitAsync(Bound);
        allocator.ExpectEverythingReturned();

        async Task NextRefusalAsync()
        {
            using var timeout = new CancellationTokenSource(Bound);
            while (true)
            {
                var (sample, count) = await observed.Reader.ReadAsync(timeout.Token);
                if (count != 0)
                {
                    Assert.Equal(1, count);
                    Assert.True(sample.Admitted);
                    Assert.InRange(sample.TextMetadataWork, 1, sample.ReclamationWork);
                    Assert.InRange(sample.ReclamationWork, 1, AutomaticMaintenance.ReclamationWorkLimit);
                    return;
                }
            }
        }
    }
}
