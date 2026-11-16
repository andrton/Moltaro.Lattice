using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// The order of writes (the remarks of ILatticeSet<TEntity> and ILatticeSet; brief of Epic 1, sprint
// S09, Acceptance and "Order"): writes to one entity are executed in the order their calls were made,
// a call made after an earlier one has returned its task is executed after it, whether or not that
// task was awaited, and writes to different entities have no order beyond their positions. A test
// makes its calls from one thread, none awaited before the next is made, and awaits them afterwards.
// - Each call meets the entity as the calls before it left it: an insert after an insert of the same
//   key is DuplicateKey, a replace after a delete NotFound, a delete after a delete changes nothing,
//   and of two conditional writes that name the same revision the first succeeds and the second is
//   RevisionConflict. A write that fails changes nothing for the writes after it, and the writes of
//   another entity made between them are not touched by it. Through the class and by name.
// - Writes submitted faster than they complete are not refused: ten thousand at once all complete,
//   and those to one entity in the order of their calls.
// What a write that changed an entity returns is a position; of one entity, later calls have later
// positions (PositionSequence), and every write takes a position of its own. A delete of an absent
// key is left out of that: the placeholder keeps the position it had for it until S12 (PositionTests).
public sealed class WriteOrderTests
{
    private const string Changed = "changed";

    private const string Unchanged = "unchanged";

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Writes_to_one_entity_made_without_awaiting_are_executed_in_the_order_of_their_calls(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        var records = database.Set("User");

        // Entity 2 exists, and its revision is the one the conditions below name.
        await records.InsertAsync(RecordOf(records, 2, "first"));
        Assert.True(records.TryGet(2L, out _, out var revision));

        // Entity 1 through its class, entity 2 by name, the calls of the two interleaved.
        (long Key, string Expected, Task<LatticeWriteResult> Write)[] calls =
        [
            (1, Changed, users.InsertAsync(UserOf(1, "inserted")).AsTask()),
            (2, Changed, records.ReplaceAsync(RecordOf(records, 2, "replaced"), revision).AsTask()),
            (1, nameof(LatticeErrorEnum.DuplicateKey), users.InsertAsync(UserOf(1, "inserted again")).AsTask()),
            (2, nameof(LatticeErrorEnum.RevisionConflict), records.ReplaceAsync(RecordOf(records, 2, "replaced with the first revision"), revision).AsTask()),
            (1, Changed, users.ReplaceAsync(UserOf(1, "replaced")).AsTask()),
            (2, nameof(LatticeErrorEnum.RevisionConflict), records.DeleteAsync(2L, revision).AsTask()),
            (1, Changed, users.DeleteAsync(1L).AsTask()),
            (1, nameof(LatticeErrorEnum.NotFound), users.ReplaceAsync(UserOf(1, "replaced after the delete")).AsTask()),
            (1, Unchanged, users.DeleteAsync(1L).AsTask()),
            (2, Changed, records.DeleteAsync(2L).AsTask()),
            (1, Changed, users.UpsertAsync(UserOf(1, "upserted")).AsTask()),
            (2, Changed, records.UpsertAsync(RecordOf(records, 2, "upserted")).AsTask()),
            (1, Changed, users.UpsertAsync(UserOf(1, "upserted again")).AsTask()),
        ];

        var outcomes = new List<string>();
        var positions = new Dictionary<long, List<ulong>> { [1] = [], [2] = [] };

        foreach (var (key, _, write) in calls)
        {
            try
            {
                var result = await write.WaitAsync(Bound);

                outcomes.Add(result.Changed ? Changed : Unchanged);

                if (result.Changed)
                {
                    positions[key].Add(PositionSequence.Of(result.Position));
                }
            }
            catch (LatticeException exception)
            {
                outcomes.Add(exception.Error.ToString());
            }
        }

        Assert.Equal(calls.Select(call => call.Expected), outcomes);
        Assert.All(positions.Values, sequences => Assert.Equal(sequences.Order(), sequences));
        Assert.Equal(positions.Values.Sum(sequences => sequences.Count), positions.Values.SelectMany(sequences => sequences).Distinct().Count());

        Assert.Equal("upserted again", users.Get(1L)?.Name);
        Assert.Equal("upserted", records.Get(2L)?["Name"]);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Ten_thousand_writes_submitted_at_once_all_complete_and_those_to_one_entity_in_the_order_of_their_calls(DatabaseImplementationEnum implementation)
    {
        // 100 entities, each inserted and then replaced 99 times; the round of a write is its Age.
        const int Entities = 100;
        const int Rounds = 100;

        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        var start = PositionSequence.Of(database.Position);
        var writes = new Task<LatticeWriteResult>[Rounds, Entities];

        for (var round = 0; round < Rounds; round++)
        {
            for (var key = 0; key < Entities; key++)
            {
                var user = UserOf(key, $"user {key}", round);

                writes[round, key] = round == 0 ? users.InsertAsync(user).AsTask() : users.ReplaceAsync(user).AsTask();
            }
        }

        // None is refused: a write that failed fails the wait.
        await Task.WhenAll(writes.Cast<Task<LatticeWriteResult>>()).WaitAsync(Bound);

        for (var key = 0; key < Entities; key++)
        {
            var sequences = Enumerable.Range(0, Rounds).Select(round => PositionSequence.Of(writes[round, key].Result.Position)).ToList();

            Assert.Equal(sequences.Order(), sequences);
            Assert.Equal(sequences.Count, sequences.Distinct().Count());
            Assert.Equal(Rounds - 1, users.Get((long)key)?.Age);
        }

        // One order of positions: every write took one of its own, and they follow the position the
        // database had before them without a gap.
        var all = writes.Cast<Task<LatticeWriteResult>>().Select(write => PositionSequence.Of(write.Result.Position)).Order();

        Assert.Equal(Enumerable.Range(1, Rounds * Entities).Select(offset => start + (ulong)offset), all);
        Assert.Equal(start + (ulong)(Rounds * Entities), PositionSequence.Of(database.Position));
    }

    private static User UserOf(long key, string name, int? age = null) => new() { Id = key, Name = name, Age = age };

    private static LatticeRecord RecordOf(ILatticeSet records, long key, string name) =>
        new(records.EntityType) { ["Id"] = key, ["Name"] = name, ["Enabled"] = false };
}
