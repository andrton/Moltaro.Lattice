using System.Globalization;
using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

public sealed class CollectionBatchRollbackTests
{
    private const ulong KeptKey = 0xFEED_0000_0000_0011UL;
    private const ulong PendingKey = 0xFEED_0000_0000_0012UL;
    private const ulong AbsentKey = 0xFEED_0000_0000_0013UL;
    private const string KeptText = "retained é€𐐨";
    private const int SegmentBytes = 65_536;

    [Theory]
    [InlineData("Utf8", false)]
    [InlineData("Utf8", true)]
    [InlineData("Utf16", false)]
    [InlineData("Utf16", true)]
    public async Task A_refused_batch_returns_prepared_collection_blocks_and_new_and_existing_text_references(
        string encoding, bool forwarded)
    {
        var schema = LatticeSchema.Compile($$"""
            {
              "StringEncoding": "{{encoding}}",
              "EntityTypes": [{
                "Id": 1, "Name": "Item",
                "Fields": [
                  { "Id": 1, "Name": "Id", "Type": "UInt64" },
                  { "Id": 2, "Name": "Label", "Type": "String", "StringStorage": "Dictionary" },
                  { "Id": 3, "Name": "Names", "Type": "Map", "Key": { "Type": "String", "StringStorage": "Dictionary" },
                    "Value": { "Type": "String", "StringStorage": "Dictionary" } }
                ],
                "PrimaryKey": [1]
              }]
            }
            """);
        var masterAllocator = new CountingNativeAllocator();
        var childAllocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(schema, ArenaWith(masterAllocator));
        var source = master.Set("Item");
        var kept = await source.InsertAsync(Record(source, KeptKey, KeptText, new Dictionary<string, string>()));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, ArenaWith(childAllocator));
        var replicas = child.Set("Item");
        Assert.Equal(kept.Position, child.Position);
        Assert.True(source.TryGet(KeptKey, out _, out var original));
        var beforeSource = HeldBy(master);
        var beforeReplica = HeldBy(child);
        var masterObtained = masterAllocator.Obtained;
        var childObtained = childAllocator.Obtained;

        var fresh = new Dictionary<string, string>(StringComparer.Ordinal) { ["also references existing text"] = KeptText };
        for (var ordinal = 0; ordinal < 320; ordinal++)
        {
            var prefix = ordinal.ToString(CultureInfo.InvariantCulture);
            fresh.Add(prefix + new string('k', 180), prefix + new string('v', 180));
        }

        var submitter = forwarded ? child : master;
        var writer = submitter.Set("Item");
        var update = writer.CreateUpdate(KeptKey);
        var pending = update.MapSet("Names", "uncommitted edit", KeptText);
        var missing = writer.CreateUpdate(AbsentKey);
        var refused = missing.Assign("Label", "must never reach storage");

        var batch = submitter.CreateWriteBatch();
        batch.Update(update, original);
        batch.Insert(Record(writer, PendingKey, KeptText, fresh));
        batch.Update(missing);
        var result = await Assert.ThrowsAsync<LatticeException>(() => batch.CommitAsync().AsTask().WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.NotFound, result.Error);
        Assert.Equal(2, result.OperationIndex);
        Assert.Throws<InvalidOperationException>(() => pending.Changed);
        Assert.Throws<InvalidOperationException>(() => refused.Changed);
        Assert.True(masterAllocator.Obtained > masterObtained, "Earlier batch members prepared no native storage.");
        Assert.Equal(childObtained, childAllocator.Obtained);

        ExpectOriginal();
        Assert.Equal(beforeSource, HeldBy(master));
        Assert.Equal(beforeReplica, HeldBy(child));

        // Commit the same value on the previously refused key, then remove it.
        // This also checks the path that did publish the same dictionary entries.
        var written = await writer.InsertAsync(Record(writer, PendingKey, KeptText, fresh));
        await child.WaitForPositionAsync(written.Position, Bound);
        foreach (var database in new[] { master, child })
        {
            var rows = database.Set("Item");
            Assert.Equal(fresh.OrderBy(entry => entry.Key, StringComparer.Ordinal),
                rows.Get(PendingKey)!.Get<Dictionary<string, string>>("Names")!.OrderBy(entry => entry.Key, StringComparer.Ordinal));
            Assert.Equal((long)(fresh.Count + 1) * 2, database.TextReferences().Sum(entry => entry.References));
            OwnershipAudit.Expect(database);
        }

        var deleted = await writer.DeleteAsync(PendingKey);
        await child.WaitForPositionAsync(deleted.Position, Bound);
        ExpectOriginal(deleted.Position);
        Assert.Equal(beforeSource, HeldBy(master));
        Assert.Equal(beforeReplica, HeldBy(child));

        await child.CloseAsync().AsTask().WaitAsync(Bound);
        await master.CloseAsync().AsTask().WaitAsync(Bound);
        masterAllocator.ExpectEverythingReturned();
        childAllocator.ExpectEverythingReturned();

        void ExpectOriginal(LatticePosition? position = null)
        {
            foreach (var database in new[] { master, child })
            {
                Assert.Equal(position ?? kept.Position, database.Position);
                var rows = database.Set("Item");
                Assert.True(rows.TryGet(KeptKey, out var owned, out var revision));
                Assert.Equal(original, revision);
                Assert.Equal(KeptText, owned.Get<string>("Label"));
                Assert.Empty(owned.Get<Dictionary<string, string>>("Names")!);
                Assert.Null(rows.Get(PendingKey));
                Assert.Null(rows.Get(AbsentKey));
                OwnershipAudit.Expect(database);
                var (bytes, references) = Assert.Single(database.TextReferences());
                var textEncoding = encoding == "Utf8" ? Encoding.UTF8 : Encoding.Unicode;
                Assert.Equal(textEncoding.GetBytes(KeptText), bytes);
                Assert.Equal(1, references);
            }
        }
    }

    private static EngineTestInputs ArenaWith(CountingNativeAllocator allocator) =>
        new()
        {
            Allocator = allocator,
            Arena = new ArenaOptions { SegmentSize = SegmentBytes, ReservedSegments = 0, MemoryLimit = 128L * SegmentBytes },
        };

    private static LatticeRecord Record(ILatticeSet set, ulong key, string label, Dictionary<string, string> values) =>
        new(set.EntityType)
        {
            ["Id"] = key,
            ["Label"] = label,
            ["Names"] = values,
        };

    private static (int Roots, int Segments, long Live, long Slack, long Prepared, long Retired) HeldBy(EngineDatabase database)
    {
        var counters = database.Counters;
        return (counters.RootEntries, counters.Arena.Segments, counters.Arena.LiveBytes, counters.Arena.ClassSlackBytes,
            counters.Arena.PreparedBytes, counters.Arena.RetiredBytes);
    }
}
