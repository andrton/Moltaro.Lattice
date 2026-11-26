using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Text;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.ResumableReclamationTests;
using static Moltaro.Lattice.Engine.Tests.Engine.Text.TextMetadataTestAccess;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// Text reachable through a root and a separator outlives a dense writer swap,
// address publication and the epochs of readers borrowing its old native bytes.
public sealed class TextLocatorLifetimeTests
{
    private const int LastId = 600;
    private const int SegmentSize = 256 * 1024;

    [Theory]
    [MemberData(nameof(TextSchema.Encodings), MemberType = typeof(TextSchema))]
    public async Task A_view_and_separator_keep_one_text_through_repacking_relocation_and_final_reclamation(string encoding)
    {
        var schema = IndexSchema.With(encoding, $$"""
            {
                "Id": 5, "Name": "Depot", "ClrType": "{{typeof(TextSchema.Depot).FullName}}",
                "Fields": [
                    { "Id": 1, "Name": "Id", "Type": "Int64" },
                    { "Id": 2, "Name": "Area", "Type": "String", "StringStorage": "Dictionary" }
                ],
                "PrimaryKey": [1]
            },
            """);
        await using var master = await ReplicationNodes.OpenMasterAsync(schema, Inputs());
        await master.Set<IndexSchema.Ranked>().InsertAsync(Ranked(1, encoding));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs());
        var rows = master.CreateWriteBatch();
        for (var index = 2; index <= LastId; index++)
        {
            rows.Insert(Ranked(index, encoding));
        }

        var staged = await rows.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(staged.Position);
        var pending = SeparatorKeys(master).Intersect(SeparatorKeys(child), StringComparer.Ordinal).Reverse().ToArray();
        Assert.NotEmpty(pending);
        var nodes = new[] { master, child };
        var find = child.Set<TextSchema.Depot>();
        var target = pending[0];
        var entity = long.Parse(target.AsSpan(target.Length - 6), CultureInfo.InvariantCulture);
        var written = await find.InsertAsync(new TextSchema.Depot { Id = 1, Area = target }).AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(written.Position);
        var ids = nodes.Select(node => TextIds(node, "Depot", "Area", [1])[1]).ToArray();
        var originals = nodes.Select((node, index) => Entry(node, ids[index])!.Value).ToArray();
        master.DrainRetired();
        child.DrainRetired();

        // Separate readers announce an older epoch to prevent publication
        // pacing from removing the filler and swapping slots too early.
        using var fillerMaster = await HeldRead.StartAsync(master, () => master.Set<IndexSchema.Ranked>().Get(entity)?.Id);
        using var fillerChild = await HeldRead.StartAsync(child, () => child.Set<IndexSchema.Ranked>().Get(entity)?.Id);

        // Keep the target entity and its neighbors so the separator survives
        // even after the target is removed.
        var discarded = new HashSet<long>();
        var vacancies = child.CreateWriteBatch();
        for (var id = 1L; id <= LastId / 2; id += 2)
        {
            if (id != entity && id != entity - 1 && id != entity + 1)
            {
                discarded.Add(id);
                vacancies.Delete<IndexSchema.Ranked>(id);
            }
        }

        var holes = await vacancies.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(holes.Position);
        // Advance the retirement cut before viewing the surviving target. Its
        // newer reader does not prevent earlier filler retirement and swaps.
        foreach (var node in nodes)
        {
            Assert.Equal(0, RetireOne(node));
        }

        var expected = TextTally.EncodingOf(encoding).GetBytes(target);
        using var masterGuard = new ViewerPause();
        using var childGuard = new ViewerPause();
        var masterReader = OwnThread.Run(() => Held(master, masterGuard, expected, target));
        var childReader = OwnThread.Run(() => Held(child, childGuard, expected, target));
        try
        {
            await masterGuard.EnteredAsync(masterReader);
            await childGuard.EnteredAsync(childReader);
            for (var index = 0; index < nodes.Length; index++)
            {
                Assert.Equal(originals[index], Entry(nodes[index], ids[index])!.Value);
            }

            await fillerMaster.ReleaseAsync();
            await fillerChild.ReleaseAsync();
            for (var index = 0; index < nodes.Length; index++)
            {
                var node = nodes[index];
                var original = originals[index];
                var native = UnderWrites(node, parts => parts.Arena);
                ExpectSparseSource(node, original.Address);
                var turns = 0;
                var after = Entry(node, ids[index])!.Value;
                while (after.Address == original.Address || after.WriterOrdinal == original.WriterOrdinal)
                {
                    Assert.True(turns++ < 32_768, "The held text did not move and change writer ordinal.");
                    _ = node.TryRunMaintenanceTurn();
                    after = Entry(node, ids[index])!.Value;
                }

                Assert.True(UnderWrites(node, _ => IsAllocated(native, original.Address)));
                Assert.Equal(target, node.Set<TextSchema.Depot>().Get(1L)?.Area);
                ExpectAccounting(node, Capacity(node));
            }
        }
        finally
        {
            masterGuard.Release();
            childGuard.Release();
            await Task.WhenAll(masterReader, childReader).WaitAsync(Bound);
        }

        foreach (var (node, index) in nodes.Select((node, index) => (node, index)))
        {
            node.DrainRetired();
            var arena = UnderWrites(node, parts => parts.Arena);
            Assert.False(UnderWrites(node, _ => IsAllocated(arena, originals[index].Address)));
            Assert.Equal(ids[index], TextIds(node, "Depot", "Area", [1])[1]);
        }

        var dropOwners = child.CreateWriteBatch();
        dropOwners.Delete<TextSchema.Depot>(1L);
        dropOwners.Delete<IndexSchema.Ranked>(entity);
        var gone = await dropOwners.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(gone.Position);
        foreach (var node in nodes)
        {
            node.DrainRetired();
            var separators = SeparatorKeys(node);
            Assert.Contains(target, separators);
            Assert.Equal(separators.Count(text => text == target), References(node, target, encoding));
            var set = node.Set<IndexSchema.Ranked>();
            Assert.Null(set.Get(entity));
            Assert.Empty(set.Index("ByLogin").Find(IndexKey.Equal(target)));
            var right = set.Index("ByLogin").Find(IndexKey.All.Range(IndexBound.Inclusive(target), IndexBound.Inclusive(TextOf(entity + 1, encoding))));
            Assert.Equal(entity + 1, Assert.Single(right).Id);
        }

        var another = await find.InsertAsync(new TextSchema.Depot { Id = 2, Area = TextOf(LastId + 1, encoding) }).AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(another.Position);
        for (var index = 0; index < nodes.Length; index++)
        {
            Assert.NotEqual(ids[index], TextIds(nodes[index], "Depot", "Area", [2])[2]);
        }

        await VerifyReuseAsync(master, child, ids, discarded, entity, target, encoding);
    }

    private static async Task VerifyReuseAsync(EngineDatabase master, EngineDatabase child, uint[] ids, HashSet<long> discarded,
        long entity, string text, string encoding)
    {
        var nodes = new[] { master, child };
        var protectedAddresses = nodes.Select((node, index) => Entry(node, ids[index])!.Value.Address).ToArray();
        using var olderMaster = await HeldRead.StartAsync(master, () => master.Set<TextSchema.Depot>().Get(2L)?.Id);
        using var olderChild = await HeldRead.StartAsync(child, () => child.Set<TextSchema.Depot>().Get(2L)?.Id);
        var removals = child.CreateWriteBatch();
        for (var id = 1L; id <= LastId; id++)
        {
            if (id != entity && !discarded.Contains(id))
            {
                removals.Delete<IndexSchema.Ranked>(id);
            }
        }

        var removed = await removals.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(removed.Position);
        var distinct = await child.Set<TextSchema.Depot>().InsertAsync(
            new TextSchema.Depot { Id = 3, Area = TextOf(LastId + 2, encoding) }).AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(distinct.Position);
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            node.DrainRetired();
            Assert.Empty(SeparatorKeys(node));
            Assert.True(References(node, text, encoding) > 0);
            Assert.NotEqual(ids[index], TextIds(node, "Depot", "Area", [3])[3]);
            Assert.True(UnderWrites(node, parts => IsAllocated(parts.Arena, protectedAddresses[index])));
        }

        await olderMaster.ReleaseAsync();
        await olderChild.ReleaseAsync();
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            node.DrainRetired();
            Assert.Equal(0L, References(node, text, encoding));
            Assert.Null(Entry(node, ids[index]));
            Assert.False(UnderWrites(node, parts => IsAllocated(parts.Arena, protectedAddresses[index])));
        }

        // Fill at least the original ID extent with live new values. A freed
        // ID can be reused in any order within that extent.
        var refilled = child.CreateWriteBatch();
        var keys = Enumerable.Range(10_000, LastId + 1).Select(id => (long)id).ToArray();
        foreach (var id in keys)
        {
            refilled.Insert(new TextSchema.Depot { Id = id, Area = TextOf(id, encoding) });
        }

        var refreshed = await refilled.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(refreshed.Position);
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            var replacement = Assert.Single(TextIds(node, "Depot", "Area", keys), item => item.Value == ids[index]);
            Assert.Equal(TextOf(replacement.Key, encoding), node.Set<TextSchema.Depot>().Get(replacement.Key)?.Area);
            Assert.Equal(0L, References(node, text, encoding));
        }
    }

    private static void Held(EngineDatabase node, ViewerPause pause, byte[] expected, string text)
    {
        var set = node.Set("Depot");
        Assert.True(set.TryGet(1L, out _, out var before));
        Assert.True(set.TryView(1L, view =>
        {
            var value = view["Area"];
            var oldPart = value.GetPart(0);
            Assert.True(oldPart.SequenceEqual(expected));
            pause.Hold();
            Assert.True(oldPart.SequenceEqual(expected));
            Assert.Equal(text, value.GetString());
            return view.Revision;
        }, out var after));
        Assert.Equal(before, after);
    }

    private static string[] SeparatorKeys(EngineDatabase node) =>
        [.. IndexContents.Of(node, "Ranked", "ByLogin").NodeKeys.Where(group => group.Level > 0).SelectMany(group => group.Keys).Select(key => (string)key[0]!)];

    private static void ExpectSparseSource(EngineDatabase node, ArenaAddress address)
    {
        Assert.True(UnderWrites(node, parts =>
        {
            var arena = parts.Arena;
            ref readonly var header = ref arena.Header(address);
            var sizeClass = SizeClasses.IndexFor((int)header.PayloadLength);
            return arena.IsRelocationSource(address, sizeClass, header.Root, header.Kind);
        }));
    }

    private static IndexSchema.Ranked Ranked(long id, string encoding) =>
        new() { Id = id, Number = id, Login = TextOf(id, encoding), Region = "" };

    // Including the native header, these classes allow 117 UTF-8 or 65
    // UTF-16 texts per 64 KiB region. The later separator is in a sparse region.
    private static string TextOf(long id, string encoding) =>
        new string('x', encoding == "Utf8" ? 520 : 480) + id.ToString("D6", CultureInfo.InvariantCulture);

    private static EngineTestInputs Inputs() => new()
    {
        IndexSeed = 243,
        Arena = new ArenaOptions { SegmentSize = SegmentSize, MemoryLimit = 16 * 1024 * 1024 },
        DisableBackgroundMaintenance = true,
    };
}
