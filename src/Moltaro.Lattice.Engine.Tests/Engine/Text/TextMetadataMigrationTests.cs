using System.Buffers;
using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Hashing;
using Moltaro.Lattice.Storage.Text;
using static Moltaro.Lattice.Engine.Tests.Engine.Text.TextMetadataTestAccess;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// Both bucket sets are writer-owned. A text remains unique while it moves,
// and removal of a known entry needs bounded work even in a colliding chain.
public sealed class TextMetadataMigrationTests
{
    private const ulong Seed = 18041;
    private const int ChainSize = 24;

    [Theory(Skip = StableLocatorNotImplemented)]
    [MemberData(nameof(TextSchema.Encodings), MemberType = typeof(TextSchema))]
    public async Task Texts_stay_unique_and_allocate_nothing_per_hit_while_colliding_entries_migrate(string encoding)
    {
        var masterAllocator = new CountingNativeAllocator();
        var childAllocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(TextSchema.Of(encoding), Inputs(masterAllocator));
        var collisions = Colliding(encoding).Select((text, index) => Depot(index + 1, text)).ToArray();
        var padding = Enumerable.Range(1, 480).Select(index => Depot(1_000 + index, $"ordinary-{index:D5}")).ToArray();
        await PutAsync(master, collisions[..1]);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(childAllocator));
        var populated = await PutAsync(master, [.. collisions[1..], .. padding]);
        await child.WaitForPositionAsync(populated);
        var nodes = new[] { (Node: master, Allocator: masterAllocator), (Node: child, Allocator: childAllocator) };
        var oldIds = nodes.Select(pair => TextIds(pair.Node, "Depot", "Area", collisions.Select(row => row.Id))).ToArray();
        foreach (var pair in nodes)
        {
            Finish(pair.Node);
        }

        var target = collisions[^1];
        var targetIds = oldIds.Select(ids => ids[target.Id]).ToArray();
        var probe = oldIds.Select((ids, index) =>
        {
            var observed = collisions.Select(row => (Row: row, Entry: Entry(nodes[index].Node, ids[row.Id])!.Value)).ToArray();
            Assert.Single(observed.Select(item => item.Entry.Bucket).Distinct());
            var deepest = observed.Max(item => item.Entry.BucketDepth);
            Assert.True(deepest >= ChainSize - 1);
            var interior = observed.Where(item => item.Row.Id != target.Id && item.Entry.BucketDepth > 0 && item.Entry.BucketDepth < deepest).ToArray();
            var near = interior.MinBy(item => item.Entry.BucketDepth);
            var far = interior.MaxBy(item => item.Entry.BucketDepth);
            Assert.True(far.Entry.BucketDepth - near.Entry.BucketDepth >= ChainSize / 2);
            return (Near: near.Row, Far: far.Row);
        }).ToArray();

        // Same seed and insertion sequence on two real roles.
        Assert.Equal((probe[0].Near.Id, probe[0].Far.Id), (probe[1].Near.Id, probe[1].Far.Id));
        var delete = child.CreateWriteBatch();
        foreach (var filler in padding)
        {
            delete.Delete<TextSchema.Depot>(filler.Id);
        }

        var trimmed = await delete.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(trimmed.Position);
        foreach (var (node, _) in nodes)
        {
            node.DrainRetired();
        }

        for (var index = 0; index < nodes.Length; index++)
        {
            var (node, allocator) = nodes[index];
            var id = targetIds[index];
            var far = oldIds[index][probe[index].Far.Id];
            var near = oldIds[index][probe[index].Near.Id];
            Until(node, () => InOld(node, id) && InOld(node, far) && InOld(node, near));
            var crossing = Capacity(node);
            Assert.NotEmpty(crossing.ActiveBuckets);
            Assert.NotEmpty(crossing.OldBuckets);
            ExpectAccounting(node, crossing);
            ExpectZeroHitAllocation(node, allocator, id, target.Area, encoding);
        }

        var refused = master.CreateWriteBatch();
        refused.Insert(Depot(3_000, target.Area));
        refused.Insert(Depot(probe[0].Far.Id, "duplicate primary key"));
        var position = master.Position;
        var error = await Assert.ThrowsAsync<LatticeException>(() => refused.CommitAsync().AsTask().WaitAsync(Bound));
        Assert.Equal((LatticeErrorEnum.DuplicateKey, 1), (error.Error, error.OperationIndex!.Value));
        Assert.Equal(position, master.Position);
        foreach (var (node, _) in nodes)
        {
            Assert.Equal(1L, References(node, target.Area, encoding));
        }

        var farWork = await RemoveAsync(master, child, probe[0].Far, oldIds);
        var nearWork = await RemoveAsync(master, child, probe[0].Near, oldIds);
        Assert.Equal(nearWork, farWork);

        for (var index = 0; index < nodes.Length; index++)
        {
            var (node, allocator) = nodes[index];
            var targetId = targetIds[index];
            var candidates = collisions
                .Select(row => (Id: oldIds[index][row.Id], Text: row.Area))
                .Where(candidate => InOld(node, candidate.Id)).ToArray();
            Assert.True(candidates.Length >= ChainSize - 3);
            foreach (var candidate in candidates)
            {
                ExpectZeroHitAllocation(node, allocator, candidate.Id, candidate.Text, encoding);
            }

            (uint Id, string Text) moved = default;
            for (var turn = 0; moved.Text is null; turn++)
            {
                Assert.True(turn < 16_384, "One original collision-chain entry did not migrate.");
                Assert.InRange(Maintain(node, 1), 0, 1);
                var nowActive = candidates.Where(candidate => Entry(node, candidate.Id)?.BucketSet == TextBucketSetEnum.Active).ToArray();
                if (nowActive.Length != 0)
                {
                    moved = Assert.Single(nowActive);
                }
            }

            Assert.NotEmpty(Capacity(node).OldBuckets);
            ExpectZeroHitAllocation(node, allocator, moved.Id, moved.Text, encoding);
            var finished = Finish(node);
            Assert.Empty(finished.OldBuckets);
            ExpectAccounting(node, finished);
            ExpectZeroHitAllocation(node, allocator, targetId, target.Area, encoding);
        }

        var reused = await child.Set<TextSchema.Depot>().InsertAsync(Depot(3_001, target.Area)).AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(reused.Position);
        foreach (var (node, _) in nodes)
        {
            Assert.Equal(2L, References(node, target.Area, encoding));
            Assert.Equal(target.Area, node.Set<TextSchema.Depot>().Get(3_001L)?.Area);
        }
    }

    private static async Task<int[]> RemoveAsync(EngineDatabase master, EngineDatabase child, TextSchema.Depot row, Dictionary<long, uint>[] ids)
    {
        var roles = new[] { master, child };
        for (var index = 0; index < roles.Length; index++)
        {
            Assert.True(InOld(roles[index], ids[index][row.Id]));
        }

        var removed = await child.Set<TextSchema.Depot>().DeleteAsync(row.Id).AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(removed.Position);
        return roles.Select((node, index) =>
        {
            var id = ids[index][row.Id];
            var spent = 0;
            for (var attempts = 0; Entry(node, id).HasValue; attempts++)
            {
                Assert.True(attempts < 512, "Final text ownership did not leave the old table.");
                spent += RetireOne(node);
            }

            Assert.True(spent > 0, "The fixture did not observe the bounded final-release path.");
            node.DrainRetired();
            return spent;
        }).ToArray();
    }

    private static bool InOld(EngineDatabase node, uint id) => Entry(node, id)?.BucketSet == TextBucketSetEnum.Old;

    private static void ExpectZeroHitAllocation(EngineDatabase node, CountingNativeAllocator allocator, uint id, string text, string encoding)
    {
        const int Hits = 64;
        var expected = Entry(node, id)!.Value;
        var codec = TextTally.EncodingOf(encoding);
        var bytes = new ReadOnlySequence<byte>(codec.GetBytes(text));
        var storage = encoding == "Utf8" ? LatticeStringEncodingEnum.Utf8 : LatticeStringEncodingEnum.Utf16;
        var requests = allocator.Requests;
        Assert.True(node.TryRunMaintenanceStep(parts =>
        {
            var table = parts.Arena.Texts;
            for (var warm = 0; warm < Hits; warm++)
            {
                Assert.True(table.TryAcquire(text, storage, (int)bytes.Length, out _, out _));
                Assert.True(table.TryAcquire(bytes, out _, out _));
                table.Release(expected.Address);
                table.Release(expected.Address);
            }

            var control = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(new byte[32]);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() > control);

            var success = true;
            long fresh = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var hit = 0; hit < Hits; hit++)
            {
                success &= table.TryAcquire(text, storage, (int)bytes.Length, out _, out var native);
                fresh += native;
                success &= table.TryAcquire(bytes, out _, out native);
                fresh += native;
            }

            var managed = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(success);
            Assert.Equal(0, fresh);
            Assert.Equal(0, managed);

            for (var hit = 0; hit < Hits * 2; hit++)
            {
                table.Release(expected.Address);
            }
        }));

        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(expected, Entry(node, id)!.Value);
        Assert.Equal(expected.References, References(node, text, encoding));
    }

    private static IEnumerable<string> Colliding(string encoding)
    {
        var codec = TextTally.EncodingOf(encoding);
        var shape = new IndexTable(12, 0);
        var produced = 0;
        for (var n = 0; produced < ChainSize; n++)
        {
            Assert.True(n < 300_000);
            var value = new string('x', 80) + n.ToString("D7", CultureInfo.InvariantCulture);
            var hash = new Marvin(Seed);
            hash.Append(codec.GetBytes(value));
            if (shape.BucketOf(hash.Finish()) == 0)
            {
                produced++;
                yield return value;
            }
        }
    }

    private static void Until(EngineDatabase node, Func<bool> condition)
    {
        for (var attempt = 0; attempt < 16_384; attempt++)
        {
            if (condition())
            {
                return;
            }

            Assert.InRange(Maintain(node, 1), 0, 1);
        }

        Assert.Fail("Bounded text maintenance did not reach the required cut.");
    }

    private static TextCapacitySnapshot Finish(EngineDatabase node)
    {
        Until(node, () => Maintain(node, 64) == 0);
        return Capacity(node);
    }

    private static async Task<LatticePosition> PutAsync(EngineDatabase node, TextSchema.Depot[] rows)
    {
        var batch = node.CreateWriteBatch();
        foreach (var row in rows)
        {
            batch.Insert(row);
        }
        return (await batch.CommitAsync().AsTask().WaitAsync(Bound)).Position;
    }

    private static TextSchema.Depot Depot(long id, string text) => new() { Id = id, Area = text };

    private static EngineTestInputs Inputs(CountingNativeAllocator allocator) => new()
    {
        Allocator = allocator,
        IndexSeed = Seed,
        DisableBackgroundMaintenance = true,
    };

}
