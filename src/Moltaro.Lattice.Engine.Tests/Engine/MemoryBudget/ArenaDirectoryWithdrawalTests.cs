using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class ArenaDirectoryWithdrawalTests
{
    [Fact]
    public async Task Cancelling_the_second_growth_restores_the_first_writes_directory_and_live_addresses()
    {
        const int grant = Arena.MaxBlockSize;
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        await using var node = await EngineMaster.OpenAsync(PieceSchema.Schema, new EngineTestInputs
        {
            ObserveBudgetBackings = true,
            Arena = new ArenaOptions { SegmentSize = grant, MemoryLimit = 128L * grant, ReservedSegments = 0 },
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
            Persistence = storage,
        });
        var pieces = node.Set<PieceSchema.Piece>();
        var seeded = await pieces.InsertAsync(PieceSchema.Of(1, 8));
        Assert.True(pieces.TryGet(1L, out _, out var revision));
        var (arena, limits) = await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var addresses = new ArenaAddress[2];
            for (var index = 0; index < addresses.Length; index++)
            {
                Assert.True(parts.Arena.TryAllocatePrepared(ValueBlocks.ChunkPayload, BlockKindEnum.Value, new RootHandle(1, 0), out addresses[index]));
                Assert.Equal((uint)index + 2, addresses[index].Segment);
                parts.Arena.Payload(addresses[index])[0] = (byte)(index + 11);
            }

            return (parts.Arena, addresses);
        });
        var initial = await OwnersAsync();
        Assert.Equal(4, initial.Segments.Length);
        Assert.Equal(136L, initial.Directory.Sum(ManagedObjectSize));
        Assert.Equal(3L * grant, initial.Bytes.NativeBytes);

        storage.Hold();
        try
        {
            var blocker = pieces.DeleteAsync(-1L).AsTask();
            await storage.NextAsync();
            var kept = PieceSchema.Of(2, 8, data: ValueBlocks.ChunkPayload);
            kept.Data![0] = 47;
            kept.Data[^1] = 97;
            var first = pieces.InsertAsync(kept).AsTask();
            var stagedFirst = await OwnersAsync();
            Assert.Equal(8, stagedFirst.Segments.Length);
            Assert.Equal(200L, stagedFirst.Directory.Sum(ManagedObjectSize));
            var firstHistory = Assert.IsAssignableFrom<object>(stagedFirst.History);
            Assert.Equal(48L, ManagedObjectSize(firstHistory));

            using var stop = new CancellationTokenSource();
            var second = pieces.InsertAsync(PieceSchema.Of(3, 8, data: 4 * ValueBlocks.ChunkPayload), stop.Token).AsTask();
            var stagedSecond = await OwnersAsync();
            Assert.Equal(16, stagedSecond.Segments.Length);
            Assert.Equal(328L, stagedSecond.Directory.Sum(ManagedObjectSize));
            var secondHistory = Assert.IsAssignableFrom<object>(stagedSecond.History);
            Assert.Same(firstHistory, Backing<object>(secondHistory, "Smaller"));
            _ = ExpectCapacity(node, allocator, out _,
                [.. initial.Directory, .. stagedFirst.Directory, .. stagedSecond.Directory, firstHistory, secondHistory]);

            var returns = allocator.ReturnedBytes;
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(Bound));
            Assert.False(first.IsCompleted);
            Assert.Equal(seeded.Position, node.Position);
            Assert.Null(pieces.Get(2L));
            Assert.Null(pieces.Get(3L));
            var restored = await OwnersAsync();
            Assert.Equal(5L * grant, allocator.ReturnedBytes - returns);
            Assert.Equal(stagedFirst.ArenaBytes, restored.ArenaBytes);
            Assert.Equal(stagedSecond.Directory.Sum(ManagedObjectSize) + ManagedObjectSize(secondHistory),
                stagedSecond.ArenaBytes - restored.ArenaBytes);
            Assert.Equal(stagedFirst.Bytes.NativeBytes, restored.Bytes.NativeBytes);
            Assert.Equal(stagedFirst.Segments.Length, restored.Segments.Length);
            Assert.All(restored.Directory.Zip(stagedFirst.Directory), pair => Assert.Same(pair.Second, pair.First));
            Assert.Equal(stagedFirst.Segments, restored.Segments);
            Assert.Same(firstHistory, restored.History);

            await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
            {
                var filler = new List<ArenaAddress>();
                var active = Backing<ArenaSegment?[]>(arena, "_segments");
                while (active.Skip(1).Any(segment => segment is null))
                {
                    Assert.True(arena.TryAllocatePrepared(ValueBlocks.ChunkPayload, BlockKindEnum.Value, new RootHandle(1, 0), out var block));
                    Assert.InRange(block.Segment, 1u, 7u);
                    filler.Add(block);
                }

                Assert.True(IndexSchema.Utf8.TryGetEntityType("Counted", out var type));
                var layout = IndexLayout.Compile(Assert.Single(type.Indexes), RootLayout.Compile(type, IndexSchema.Utf8));
                var reserve = new NodePool(layout, arena, parts.Retired);
                var beforeTopUp = allocator.OutstandingBytes;
                Assert.False(arena.Budget!.IsPreparing);
                reserve.TopUp();
                Assert.True(reserve.Count > 0);
                Assert.Equal(8u, Backing<ArenaAddress>(reserve, "_head").Segment);
                Assert.Equal(beforeTopUp + grant, allocator.OutstandingBytes);
                var larger = Backing<ArenaSegment?[]>(arena, "_segments");
                Assert.Equal(16, larger.Length);
                var topUpHistory = Backing<object>(arena, "_smallerDirectory");
                _ = ExpectCapacity(node, allocator, out _, [larger, topUpHistory, firstHistory, .. restored.Directory]);

                reserve.GiveBackReserve();
                Assert.Equal(0, reserve.Count);
                Assert.Equal(beforeTopUp, allocator.OutstandingBytes);
                Assert.Same(stagedFirst.Directory[0], Backing<ArenaSegment?[]>(arena, "_segments"));
                Assert.Same(firstHistory, Backing<object>(arena, "_smallerDirectory"));
                foreach (var block in filler)
                {
                    arena.Free(block);
                }

                return true;
            });
            var afterTopUp = await OwnersAsync();
            Assert.Equal(restored.ArenaBytes, afterTopUp.ArenaBytes);
            Assert.Equal(restored.Bytes.NativeBytes, afterTopUp.Bytes.NativeBytes);

            storage.ReleaseAll();
            var prior = await blocker.WaitAsync(Bound);
            var published = await first.WaitAsync(Bound);
            Assert.NotEqual(prior.Position, published.Position);
            Assert.Equal(published.Position, node.Position);
            Assert.Equal(kept.Data, pieces.Get(2L)?.Data);
            Assert.Null(pieces.Get(3L));
            Assert.True(pieces.TryGet(1L, out _, out var still));
            Assert.Equal(revision, still);
            var installed = await OwnersAsync();
            Assert.All(installed.Directory.Zip(stagedFirst.Directory), pair => Assert.Same(pair.Second, pair.First));
            Assert.Null(installed.History);

            await MaintenanceStep.RunAfterAdmissionAsync(node, _ =>
            {
                for (var index = 0; index < limits.Length; index++)
                {
                    Assert.Equal((byte)(index + 11), arena.Payload(limits[index])[0]);
                    arena.Free(limits[index]);
                }

                return true;
            });
            OwnershipAudit.Expect(node);
        }
        finally
        {
            storage.ReleaseAll();
        }

        Task<(object[] Directory, ArenaSegment?[] Segments, object? History, BudgetCapacitySample Bytes, long ArenaBytes)> OwnersAsync() =>
            MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
            {
                Assert.Same(arena, parts.Arena);
                var segments = Backing<ArenaSegment?[]>(arena, "_segments");
                object[] directory = [segments, Backing<int[]>(arena, "_freedSegmentSlots"), Backing<int[]>(arena, "_reservedSegments")];
                var history = Backing<object?>(arena, "_smallerDirectory");
                var arenaBytes = 0L;
                node.InspectBudget((owner, backing) =>
                {
                    if (owner == nameof(Arena))
                    {
                        arenaBytes += ManagedObjectSize(backing);
                    }
                });

                return (directory, segments.ToArray(), history, ExpectCapacity(node, allocator, out _, directory), arenaBytes);
            });
    }
}
