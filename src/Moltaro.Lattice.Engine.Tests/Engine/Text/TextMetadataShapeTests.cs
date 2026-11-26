using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Text;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.Engine.Tests.Engine.Text.TextMetadataTestAccess;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// Writer capacity follows the live set independently of the stable IDs' spread.
// Directory, leaf, writer, active bucket, old bucket and reader-held bytes are
// separate in the storage accounting (arena-protocol.md; storage-and-maintenance.md).
public sealed class TextMetadataShapeTests
{
    private const int IdsPerLeaf = 64;
    private const int Leaves = 4;
    private const int Entries = Leaves * IdsPerLeaf;

    [Theory]
    [MemberData(nameof(TextSchema.Encodings), MemberType = typeof(TextSchema))]
    public async Task Sparse_ids_keep_one_small_locator_leaf_each_and_the_same_writer_room_as_dense_ids(string encoding)
    {
        var schema = TextSchema.Of(encoding);
        await using var master = await ReplicationNodes.OpenMasterAsync(schema, Inputs(101));
        var prepared = Enumerable.Range(1, Entries).Select(id => Depot(id)).ToArray();
        await PutAsync(master, prepared[..1]);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(313));
        var loaded = await PutAsync(master, prepared[1..]);
        await child.WaitForPositionAsync(loaded);

        var initialMaster = EntryIds(master, prepared);
        var initialChild = EntryIds(child, prepared);
        var survivors = initialMaster.GroupBy(entry => entry.Id / IdsPerLeaf).Take(Leaves).Select(group => group.First().Key).ToHashSet();
        Assert.Equal(Leaves, survivors.Count);
        Assert.Equal(Leaves, initialChild.Where(entry => survivors.Contains(entry.Key)).Select(entry => entry.Id / IdsPerLeaf).Distinct().Count());

        var expandedMaster = Settle(master);
        var expandedChild = Settle(child);
        var delete = child.CreateWriteBatch();
        foreach (var item in prepared)
        {
            if (!survivors.Contains(item.Id))
            {
                delete.Delete<TextSchema.Depot>(item.Id);
            }
        }

        var removed = await delete.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(removed.Position);
        var expected = prepared.Where(item => survivors.Contains(item.Id)).ToArray();
        var freshMaster = await OpenFreshAsync(schema, expected, 101);
        var freshChild = await OpenFreshAsync(schema, expected, 313);
        var one = await OpenFreshAsync(schema, expected[..1], 997);

        ExpectSparse(master, initialMaster, expected, expandedMaster, freshMaster, one, encoding);
        ExpectSparse(child, initialChild, expected, expandedChild, freshChild, one, encoding);
    }

    [Theory]
    [MemberData(nameof(TextSchema.Encodings), MemberType = typeof(TextSchema))]
    public Task A_reader_local_keeps_one_old_leaf_after_its_smaller_replacement_is_published(string encoding) =>
        LocatorReplacementAsync(encoding, inspectBudget: false);

    [Theory(Skip = Pending)]
    [MemberData(nameof(TextSchema.Encodings), MemberType = typeof(TextSchema))]
    public Task Managed_text_capacity_charges_both_bucket_generations_and_only_the_owned_locator_leaf(string encoding) =>
        LocatorReplacementAsync(encoding, inspectBudget: true);

    private static async Task LocatorReplacementAsync(string encoding, bool inspectBudget)
    {
        var schema = TextSchema.Of(encoding);
        var allocator = new CountingNativeAllocator();
        await using var node = inspectBudget
            ? await OpenAsync(64L * Segment, allocator, schema: schema)
            : await EngineMaster.OpenAsync(schema, Inputs(101));
        var values = Enumerable.Range(1, IdsPerLeaf - 1).Select(Depot).ToArray();
        await PutAsync(node, values);
        var dense = Settle(node);
        var keeper = values[0];
        var id = TextIds(node, "Depot", "Area", [keeper.Id])[keeper.Id];
        var before = CaptureLeaf(node, id);
        Assert.Equal(Bytes(dense.LocatorLeaves), Bytes(before.Select(allocation => allocation.Shape).ToArray()));
        if (inspectBudget)
        {
            _ = ExpectCapacity(node, allocator, out _, before.Select(allocation => allocation.Identity).ToArray());
        }

        var delete = node.CreateWriteBatch();
        foreach (var other in values[1..])
        {
            delete.Delete<TextSchema.Depot>(other.Id);
        }

        await delete.CommitAsync().AsTask().WaitAsync(Bound);
        if (inspectBudget)
        {
            node.DrainRetired();
            Assert.Equal(1, Maintain(node, 1));
            var buckets = UnderWrites(node, parts =>
            {
                var texts = parts.Arena.Texts;
                var old = Assert.IsType<TextBucketSet>(Backing<TextBucketSet?>(texts, "_old"));
                var active = Backing<TextBucketSet>(texts, "_active");
                return new[] { old, active };
            });
            var migrating = Capacity(node);
            Assert.NotEmpty(migrating.OldBuckets);
            Assert.NotEmpty(migrating.ActiveBuckets);
            _ = ExpectCapacity(node, allocator, out _, buckets.SelectMany(BucketObjects).ToArray());
        }

        var sparse = Settle(node);
        var current = CaptureLeaf(node, id);
        var currentObjects = current.Select(allocation => allocation.Identity).ToHashSet(ReferenceEqualityComparer.Instance);
        var oldOnly = before.Where(allocation => !currentObjects.Contains(allocation.Identity)).Select(allocation => allocation.Shape).ToArray();
        Assert.NotEmpty(oldOnly);
        Assert.Equal(Bytes(sparse.LocatorLeaves), Bytes(current.Select(allocation => allocation.Shape).ToArray()));
        Assert.True(Bytes(sparse.LocatorLeaves) < Bytes(dense.LocatorLeaves));
        Assert.True(Bytes(oldOnly) > 0);

        // This holder represents a managed local at the locator acquire.
        // Its objects are outside the engine's current-tree ledger.
        ExpectAccounting(node, sparse);
        Assert.Empty(sparse.RetiredLocator);
        Assert.Equal(keeper.Area, node.Set<TextSchema.Depot>().Get(keeper.Id)?.Area);
        if (inspectBudget)
        {
            _ = ExpectCapacity(node, allocator, out _, currentObjects.ToArray());
            _ = node.InspectBudget((_, backing) =>
            {
                foreach (var former in before.Where(allocation => !currentObjects.Contains(allocation.Identity)))
                {
                    Assert.NotSame(former.Identity, backing);
                }
            });
        }

        GC.KeepAlive(before);
    }

    internal static IEnumerable<object> BucketObjects(TextBucketSet set)
    {
        yield return set;
        var pages = Backing<object>(set, "_pages");
        yield return pages;
        foreach (var owner in TreeObjects(Backing<object?>(pages, "_root")))
        {
            yield return owner;
        }

        static IEnumerable<object> TreeObjects(object? root)
        {
            if (root is null)
            {
                yield break;
            }

            yield return root;
            if (root.GetType().GetField("Heads") is { } field)
            {
                yield return field.GetValue(root)!;
                yield break;
            }

            var children = Backing<object?[]>(root, "Children");
            yield return children;
            foreach (var child in children.SelectMany(TreeObjects))
            {
                yield return child;
            }
        }
    }

    private static IReadOnlyList<TextLocatorAllocation> CaptureLeaf(EngineDatabase node, uint id) =>
        UnderWrites(node, parts =>
        {
            Assert.True(parts.Arena.Texts.TryCaptureLocatorLeaf(id, out var actual));
            return actual;
        });

    private static async Task<TextCapacitySnapshot> OpenFreshAsync(LatticeSchema schema, TextSchema.Depot[] entries, ulong seed)
    {
        await using var node = await EngineMaster.OpenAsync(schema, Inputs(seed));
        await PutAsync(node, entries);
        return Settle(node);
    }

    private static void ExpectSparse(EngineDatabase node, (long Key, uint Id)[] initialIds, TextSchema.Depot[] expected,
        TextCapacitySnapshot expanded, TextCapacitySnapshot dense, TextCapacitySnapshot singleton, string encoding)
    {
        var sparse = Settle(node);
        var expectedKeys = expected.Select(item => item.Id).ToHashSet();
        var survivingIds = EntryIds(node, expected);
        Assert.Equal(initialIds.Where(entry => expectedKeys.Contains(entry.Key)), survivingIds);
        Assert.Equal(Leaves, survivingIds.Select(entry => entry.Id / IdsPerLeaf).Distinct().Count());

        var sparseLeaves = Bytes(sparse.LocatorLeaves);
        Assert.Equal(Leaves * Bytes(singleton.LocatorLeaves), sparseLeaves);
        Assert.True(sparseLeaves < Bytes(expanded.LocatorLeaves));
        Assert.Equal(Bytes(dense.WriterMetadata), Bytes(sparse.WriterMetadata));
        Assert.Equal(Bytes(dense.ActiveBuckets), Bytes(sparse.ActiveBuckets));
        Assert.Empty(sparse.OldBuckets);
        Assert.Empty(sparse.RetiredLocator);
        ExpectAccounting(node, sparse);

        var set = node.Set<TextSchema.Depot>();
        Assert.All(expected, item => Assert.Equal(item.Area, set.Get(item.Id)?.Area));
        var counts = EntryCounts.Of(node, encoding);
        Assert.Equal(expected.Select(item => item.Area).Order(), counts.Keys.Order());
        Assert.All(counts.Values, count => Assert.Equal(1L, count));
    }

    private static (long Key, uint Id)[] EntryIds(EngineDatabase node, TextSchema.Depot[] values) =>
        [.. TextIds(node, "Depot", "Area", values.Select(item => item.Id)).OrderBy(entry => entry.Key).Select(entry => (entry.Key, entry.Value))];

    private static async Task<LatticePosition> PutAsync(EngineDatabase node, TextSchema.Depot[] values)
    {
        var batch = node.CreateWriteBatch();
        foreach (var item in values)
        {
            batch.Insert(item);
        }

        return (await batch.CommitAsync().AsTask().WaitAsync(Bound)).Position;
    }

    private static TextSchema.Depot Depot(int id) => new()
    {
        Id = id,
        Area = new string('t', id % 2 == 0 ? 128 : 4_400) + "é" + id.ToString("D5", CultureInfo.InvariantCulture),
    };

    private static EngineTestInputs Inputs(ulong seed) => new() { IndexSeed = seed, DisableBackgroundMaintenance = true };

    private static TextCapacitySnapshot Settle(EngineDatabase node)
    {
        node.DrainRetired();
        for (var turn = 0; turn < Entries * Entries; turn++)
        {
            var used = -1;
            TextCapacitySnapshot snapshot = default;
            if (!node.TryRunMaintenanceStep(parts =>
            {
                used = parts.Arena.Texts.Maintain(64);
                snapshot = parts.Arena.Texts.MeasureCapacity();
            }))
            {
                continue;
            }

            Assert.InRange(used, 0, 64);
            if (used == 0)
            {
                Assert.Empty(snapshot.OldBuckets);
                return snapshot;
            }
        }

        throw new InvalidOperationException("The metadata did not settle with a fixed set of live texts.");
    }

}
