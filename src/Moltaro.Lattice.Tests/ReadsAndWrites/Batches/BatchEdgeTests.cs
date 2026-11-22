using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Tests.ReadsAndWrites.Batches;

// The edges of a write batch (the brief of Epic 1, sprint S17; decision O18 and the remarks of
// ILatticeWriteBatch): a batch has no limit of its own and is executed as one write at one position
// whatever it holds. A batch of one member takes a position and gives a revision as a single write does.
// A batch takes one operation per entity, whatever the kinds of the two operations. A batch larger than
// the group the commit stage forms, 32 writes or 8 MiB (docs/design/write-path.md), and one of 10,000
// small entities with indexes under the default options, are each executed as one, at one position. An
// empty batch is refused (WriteBatchTests).
public sealed class BatchEdgeTests
{
    private const int Mebibyte = 1024 * 1024;

    public static IEnumerable<TheoryDataRow<string, string>> PairsOfKinds =>
        from first in Kinds
        from second in Kinds
        select new TheoryDataRow<string, string>(first, second);

    private static string[] Kinds => ["insert", "replace", "upsert", "delete"];

    [Fact(Skip = SkipReason.WriteBatch)]
    public async Task Batch_of_one_member_takes_one_position_and_gives_a_revision_as_a_single_write_does()
    {
        await using var database = await TestData.OpenMasterAsync();
        var users = database.Set<User>();
        var before = await users.InsertAsync(new User { Id = 2, Name = "Grace" });

        var batch = database.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });
        var result = await batch.CommitAsync();

        Assert.True(result.Changed);
        Assert.Equal(PositionSequence.Of(before.Position) + 1, PositionSequence.Of(result.Position));
        Assert.Equal(result.Position, database.Position);
        Assert.True(users.TryGet(1L, out var ada, out var revision));

        // The revision of the member is a condition on it, and its next state has another.
        ada.Name = "Ada Lovelace";
        var replaced = await users.ReplaceAsync(ada, revision);

        Assert.Equal(PositionSequence.Of(result.Position) + 1, PositionSequence.Of(replaced.Position));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => users.ReplaceAsync(ada, revision));
    }

    [Theory]
    [MemberData(nameof(PairsOfKinds))]
    public async Task Second_operation_for_one_entity_is_refused_whatever_the_two_kinds(string first, string second)
    {
        await using var database = await TestData.OpenMasterAsync();
        var batch = database.CreateWriteBatch();

        Add(batch, first, new User { Id = 1, Name = "Ada" });

        Assert.Throws<ArgumentException>(() => Add(batch, second, new User { Id = 1, Name = "Ada Lovelace" }));
        Assert.Equal(1, batch.Count);
    }

    [Fact(Skip = SkipReason.WriteBatch)]
    public async Task Batch_of_more_writes_than_a_group_holds_is_executed_at_one_position()
    {
        await using var database = await TestData.OpenMasterAsync();
        var users = database.Set<User>();
        var before = database.Position;

        var batch = database.CreateWriteBatch();

        for (var id = 1L; id <= 100; id++)
        {
            batch.Insert(new User { Id = id, Name = $"user {id}", Email = $"user{id}@example.org", Age = (int)(id % 40) });
        }

        var result = await batch.CommitAsync();

        Assert.Equal(PositionSequence.Of(before) + 1, PositionSequence.Of(result.Position));
        Assert.Equal(100, users.Enumerate().Count());
        Assert.Equal(["user 7"], users.Index("ByEmail").Find(IndexKey.Equal("user7@example.org")).Select(user => user.Name));
    }

    [Fact(Skip = SkipReason.WriteBatch)]
    public async Task Batch_of_more_bytes_than_a_group_holds_is_executed_at_one_position()
    {
        await using var database = await TestData.OpenMasterAsync();
        var settings = database.Set("Setting");
        var before = database.Position;

        var batch = database.CreateWriteBatch();

        for (var index = 0; index < 4; index++)
        {
            batch.Insert(new LatticeRecord(settings.EntityType)
            {
                ["Name"] = $"large {index}",
                ["Value"] = "value",
                ["Priority"] = index,
                ["Payload"] = new byte[3 * Mebibyte],
            });
        }

        var result = await batch.CommitAsync();

        Assert.Equal(PositionSequence.Of(before) + 1, PositionSequence.Of(result.Position));
        Assert.All(Enumerable.Range(0, 4), index => Assert.Equal(3 * Mebibyte, ((byte[])settings.Get($"large {index}")!["Payload"]!).Length));
    }

    [Fact(Skip = SkipReason.WriteBatch)]
    public async Task Batch_of_ten_thousand_small_entities_with_indexes_is_executed_as_one_under_the_default_options()
    {
        const int Count = 10_000;

        await using var database = await TestData.OpenMasterAsync();
        var users = database.Set<User>();
        var before = database.Position;

        var batch = database.CreateWriteBatch();

        for (var id = 1L; id <= Count; id++)
        {
            batch.Insert(new User { Id = id, Name = $"user {id}", Email = $"user{id}@example.org", Age = (int)(id % 90) });
        }

        var result = await batch.CommitAsync();

        Assert.Equal(PositionSequence.Of(before) + 1, PositionSequence.Of(result.Position));
        Assert.Equal(Count, users.Enumerate().Count());
        Assert.Equal(Count / 90, users.Index("ByAgeAndName").Find(IndexKey.Equal(17)).Count());

        var next = await users.DeleteAsync(1L);

        Assert.Equal(PositionSequence.Of(result.Position) + 1, PositionSequence.Of(next.Position));
    }

    private static void Add(ILatticeWriteBatch batch, string kind, User user)
    {
        switch (kind)
        {
            case "insert":
                batch.Insert(user);
                break;

            case "replace":
                batch.Replace(user);
                break;

            case "upsert":
                batch.Upsert(user);
                break;

            default:
                batch.Delete<User>(user.Id);
                break;
        }
    }
}
