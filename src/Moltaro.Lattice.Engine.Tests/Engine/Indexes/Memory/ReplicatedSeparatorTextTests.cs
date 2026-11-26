using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Text;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Memory;

// A B+tree separator owns the text it compares after its leaf and source entity
// are gone (docs/decisions/index-structures.md, "Search-key ownership").
public sealed class ReplicatedSeparatorTextTests
{
    private const int LastId = 600;

    [Theory]
    [MemberData(nameof(IndexSchema.Encodings), MemberType = typeof(IndexSchema))]
    public async Task Both_nodes_find_the_right_range_through_a_separator_after_its_last_entity_is_reclaimed(string encoding)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(IndexSchema.Of(encoding), new EngineTestInputs { DisableBackgroundMaintenance = true });
        await master.Set<IndexSchema.Ranked>().InsertAsync(Ranked(1));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { DisableBackgroundMaintenance = true });
        var write = master.CreateWriteBatch();
        for (var id = 2L; id <= LastId; id++)
        {
            write.Insert(Ranked(id));
        }

        var populated = await write.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(populated.Position);
        var original = SeparatorKeys(child).Intersect(SeparatorKeys(master), StringComparer.Ordinal).Reverse().ToArray();
        Assert.NotEmpty(original);

        var deleted = new HashSet<long>();
        foreach (var possibleSeparator in original)
        {
            var id = long.Parse(possibleSeparator.AsSpan(80), CultureInfo.InvariantCulture);
            Assert.True(deleted.Add(id));
            var writeResult = await child.Set<IndexSchema.Ranked>().DeleteAsync(id).AsTask().WaitAsync(Bound);
            Assert.Equal(writeResult.Position, master.Position);
            await child.WaitForPositionAsync(writeResult.Position);
            master.DrainRetired();
            child.DrainRetired();

            if (!SeparatorKeys(master).Contains(possibleSeparator) || !SeparatorKeys(child).Contains(possibleSeparator))
            {
                continue;
            }

            var next = Enumerable.Range((int)id + 1, LastId - (int)id).First(neighbor => !deleted.Contains(neighbor));
            foreach (var node in new[] { master, child })
            {
                var set = node.Set<IndexSchema.Ranked>();
                var index = set.Index("ByLogin");
                Assert.Equal(0, node.Counters.Arena.RetiredBytes);
                Assert.Null(set.Get(id));
                Assert.Empty(index.Find(IndexKey.Equal(possibleSeparator)));
                var afterSeparator = IndexKey.All.Range(IndexBound.Inclusive(possibleSeparator), IndexBound.Inclusive(TextOf(next)));
                Assert.Equal((long)next, Assert.Single(index.Find(afterSeparator)).Id);
                Assert.Equal(SeparatorKeys(node).Count(key => key == possibleSeparator), EntryCounts.Of(node, encoding)[possibleSeparator]);
                OwnershipAudit.Expect(node);
            }

            return;
        }

        Assert.Fail("Every shared separator disappeared when its last entity was deleted.");
    }

    private static string[] SeparatorKeys(EngineDatabase node) =>
        [.. IndexContents.Of(node, "Ranked", "ByLogin").NodeKeys.Where(group => group.Level > 0).SelectMany(group => group.Keys).Select(key => (string)key[0]!)];

    private static IndexSchema.Ranked Ranked(long id) => new() { Id = id, Number = id, Login = TextOf(id), Region = "" };

    // Every head and length is equal, so a separator comparison must examine
    // the shared stored bytes beyond its cached prefix.
    private static string TextOf(long id) => new string('x', 80) + id.ToString("D6", CultureInfo.InvariantCulture);
}
