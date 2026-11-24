using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// An ownership audit after churn finds every block owned by exactly one live entity (OwnershipAudit): after
// writes of every kind and every shape of TreeSchema over a few keys, among them writes that fail at every
// place a write can fail and leave nothing behind:
// - refused for memory: an arena of three segments of 4 MiB refuses a tree of shape 3, about 6 MB, while
//   another one is held, and refuses some batches of large trees whole;
// - refused by their condition: an insert of a key that exists, a replace of a key that does not, a replace
//   with a revision another write has replaced;
// - refused by the validation of the entity at depth, before they reach the engine;
// - a write batch refused whole for one failing member;
// - withdrawn: a write admitted and waiting behind a commit that the persistence step holds, whose token is
//   cancelled before its dispatch, and which releases what it prepared (HeldPersistence).
// The operations are drawn from a generator with a fixed seed, so a failure repeats; the audit runs after
// every round.
public sealed class OwnershipAfterChurnTests
{
    private const int Rounds = 30;

    private const int OperationsPerRound = 12;

    private const int Keys = 8;

    [Fact]
    public async Task Ownership_audit_after_churn_with_refusals_and_withdrawals_finds_every_block_owned_by_exactly_one_live_entity()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await EngineMaster.OpenAsync(TreeSchema.Schema, new EngineTestInputs
        {
            Arena = new ArenaOptions { MemoryLimit = 3L * 4 * 1024 * 1024 },
            Persistence = storage,
        });
        var random = new Random(18);
        var outcomes = new Dictionary<string, int>();

        try
        {
            var items = database.Set("Item");

            for (var round = 0; round < Rounds; round++)
            {
                for (var operation = 0; operation < OperationsPerRound; operation++)
                {
                    var outcome = await OperateAsync(database, items, random, round * OperationsPerRound + operation);

                    outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
                }

                await WithdrawAsync(database, storage, items, random, round);
                OwnershipAudit.Expect(database);
            }

            // Every way a write ends was met.
            foreach (var outcome in new[] { "written", "deleted", "Memory", "DuplicateKey", "NotFound", "RevisionConflict", "ValidationFailed", "batch written", "batch refused" })
            {
                Assert.True(outcomes.GetValueOrDefault(outcome) > 0, $"No operation ended as {outcome}: {string.Join(", ", outcomes)}.");
            }
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // One operation drawn from the generator, and how it ended.
    private static async Task<string> OperateAsync(EngineDatabase database, ILatticeSet items, Random random, int number)
    {
        var key = (long)random.Next(Keys);
        var shape = random.Next(TreeSchema.Shapes);

        try
        {
            switch (random.Next(9))
            {
                case 0:
                    await items.InsertAsync(TreeSchema.RecordOf(items, key, shape, number));
                    return "written";
                case 1:
                    await items.ReplaceAsync(TreeSchema.RecordOf(items, key, shape, number));
                    return "written";
                case 2 or 3:
                    await items.UpsertAsync(TreeSchema.RecordOf(items, key, shape, number));
                    return "written";
                case 4:
                    return (await items.DeleteAsync(key)).Changed ? "deleted" : "nothing to delete";
                case 5:
                    // A revision that no state of the entity has any more once it is replaced.
                    if (items.TryGet(key, out _, out var revision))
                    {
                        await items.UpsertAsync(TreeSchema.RecordOf(items, key, 1, number));
                        await items.ReplaceAsync(TreeSchema.RecordOf(items, key, shape, number), revision);
                    }

                    return "written";
                case 6:
                    // A null in a List of an owned value, which the validation refuses at depth.
                    var invalid = TreeSchema.RecordOf(items, key, 2, number);
                    invalid.Get<List<LatticeOwnedValue>>("Places")![0].Get<List<string>>("Lines")!.Add(null!);
                    await items.UpsertAsync(invalid);
                    return "written";
                case 7:
                    return await BatchAsync(database, items, random, number, failing: false);
                default:
                    return await BatchAsync(database, items, random, number, failing: true);
            }
        }
        catch (LatticeException exception)
        {
            return exception.Limit is { } limit ? limit.ToString() : exception.Error.ToString();
        }
    }

    // A batch of two to four writes of other keys, or of the same key with an insert of a key that exists
    // as its failing member.
    private static async Task<string> BatchAsync(EngineDatabase database, ILatticeSet items, Random random, int number, bool failing)
    {
        var batch = database.CreateWriteBatch();
        var keys = Enumerable.Range(0, Keys).Select(key => (long)key).OrderBy(_ => random.Next()).Take(random.Next(2, 5)).ToArray();

        foreach (var key in keys)
        {
            batch.Upsert(TreeSchema.RecordOf(items, key, random.Next(TreeSchema.Shapes), number));
        }

        if (failing)
        {
            await items.UpsertAsync(TreeSchema.RecordOf(items, keys[0] + Keys, 1, number));
            batch.Insert(TreeSchema.RecordOf(items, keys[0] + Keys, 1, number));
        }

        try
        {
            await batch.CommitAsync();

            return "batch written";
        }
        catch (LatticeException)
        {
            return "batch refused";
        }
    }

    // A write admitted and prepared behind a commit the persistence step holds, withdrawn by its token before
    // its dispatch; the held commit, a delete, which needs no memory, is then released and completes. The
    // withdrawn write is of shape 1, which a full arena still holds.
    private static async Task WithdrawAsync(EngineDatabase database, HeldPersistence storage, ILatticeSet items, Random random, int round)
    {
        storage.Hold();

        var held = items.DeleteAsync((long)random.Next(Keys)).AsTask();
        var commit = await storage.NextAsync();

        using var cancellation = new CancellationTokenSource();
        var withdrawn = items.UpsertAsync(TreeSchema.RecordOf(items, random.Next(Keys), 1, round), cancellation.Token).AsTask();

        await CountersWait.UntilAsync(database, counters => counters.QueuedWrites == 2, "the write to withdraw is admitted behind the held one");
        cancellation.Cancel();
        commit.Release();
        storage.ReleaseAll();

        await held.WaitAsync(Bound);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawn.WaitAsync(Bound));
    }
}
