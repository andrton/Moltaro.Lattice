using Moltaro.Lattice.Engine.Tests.Fixture;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches;

// The bound of the reservation of a batch does not refuse a batch that fits (the brief of Epic 1, sprint S17,
// point 5): with the default options, a batch of 10,000 users of the reference fixture, with the three text
// indexes of its scalar form, ByLogin, ByEmail and ByRegionAndLogin, and the same batch with the Unique marks
// of that form, is executed as one write at one position. The users are the first 10,000 the fixture
// generates (GeneratedRecords), whose schema the database is opened with.
public sealed class FixtureBatchTests
{
    private const int Users = 10_000;

    public static IEnumerable<TheoryDataRow<string>> Forms => [new(FixtureForms.Scalar), new(FixtureForms.ScalarUnique)];

    [Theory(Skip = SkipReason.WriteBatch)]
    [MemberData(nameof(Forms))]
    public async Task Batch_of_ten_thousand_users_of_the_fixture_with_their_indexes_is_executed_as_one_under_the_default_options(string form)
    {
        var (schema, _) = GeneratedRecords.Of(form);
        var users = GeneratedRecords.Of(form, "User").Take(Users).ToList();
        await using var database = await EngineMaster.OpenAsync(schema);
        var before = database.Position;

        var batch = database.CreateWriteBatch();

        foreach (var user in users)
        {
            batch.Insert(user);
        }

        var result = await batch.CommitAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(PositionSequence.Of(before) + 1, PositionSequence.Of(result.Position));
        Assert.Equal(Users, database.Set("User").Enumerate().Count());
        Assert.Equal(Users, database.Counters.RootEntries);
    }
}
