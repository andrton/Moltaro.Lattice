using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Tests.Indexes.Unique;

// Of many writers of one value of a unique index at the same time, exactly one succeeds; every other is
// refused with UniqueConstraintViolation and the name of the index, and afterwards a search for the value
// finds the one root that holds it (decision on unique indexes, "Two independent concurrent writes for the
// same unique value cannot both succeed"). The writers are inserts of new entities, replaces of entities
// that move their key onto the value, or both, each on a thread of its own, all let go at once, in rounds
// of a new value each. Accounts of UniqueSchemas.
public sealed class ConcurrentWritersTests
{
    private const int Writers = 16;

    private const int Rounds = 20;

    [Theory(Skip = SkipReason.UniqueIndexes)]
    [InlineData("inserts")]
    [InlineData("replaces")]
    [InlineData("inserts and replaces")]
    public async Task Of_many_writers_of_one_value_exactly_one_succeeds_every_other_is_refused_and_a_search_finds_one_owner(string writers)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Accounts);
        var accounts = database.Set<Account>();

        // The entities a replace moves, each with a login of its own.
        for (var writer = 1; writer <= Writers; writer++)
        {
            await accounts.InsertAsync(new Account { Id = writer, Login = $"own-{writer}", Tenant = "acme", Email = $"{writer}@example.org" });
        }

        for (var round = 0; round < Rounds; round++)
        {
            var value = $"taken-{round}";
            using var start = new ManualResetEventSlim();
            var writes = new List<(long Id, Task<LatticeWriteResult> Write)>();

            for (var writer = 1; writer <= Writers; writer++)
            {
                var replaces = writers == "replaces" || (writers == "inserts and replaces" && writer % 2 == 0);
                var account = replaces
                    ? new Account { Id = writer, Login = value, Tenant = "acme", Email = $"{writer}@example.org" }
                    : new Account { Id = (1000 * (round + 1)) + writer, Login = value, Tenant = "acme", Email = $"{round}-{writer}@example.org" };

                writes.Add((account.Id, Task.Run(async () =>
                {
                    start.Wait(Bound);

                    return replaces ? await accounts.ReplaceAsync(account) : await accounts.InsertAsync(account);
                })));
            }

            start.Set();

            var succeeded = new List<long>();

            foreach (var (id, write) in writes)
            {
                var failure = await Record.ExceptionAsync(() => write.WaitAsync(Bound));

                if (failure is null)
                {
                    succeeded.Add(id);

                    continue;
                }

                var refused = Assert.IsType<LatticeException>(failure);

                Assert.Equal(LatticeErrorEnum.UniqueConstraintViolation, refused.Error);
                Assert.Equal("ByLogin", refused.IndexName);
            }

            var owner = Assert.Single(succeeded);

            Assert.Equal([owner], UniqueSchemas.IdsOf(accounts.Index("ByLogin").Find(IndexKey.Equal(value))));
            Assert.Equal(value, accounts.Get(owner)!.Login);
        }

        // Every entity a replace did not move keeps its own login.
        for (var writer = 1; writer <= Writers; writer++)
        {
            var login = accounts.Get((long)writer)!.Login;

            Assert.True(login == $"own-{writer}" || login.StartsWith("taken-", StringComparison.Ordinal), $"Account {writer} holds the login {login}.");
        }
    }
}
