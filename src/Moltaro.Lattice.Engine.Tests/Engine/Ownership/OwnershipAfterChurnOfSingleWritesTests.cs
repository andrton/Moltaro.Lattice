using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// An ownership audit after churn of single writes finds every block owned by exactly one live entity
// (OwnershipAudit): after inserts, replaces, upserts and deletes of every shape of TreeSchema over a few keys,
// among them writes that fail at every place a single write can fail and leave nothing behind:
// - refused for memory: an arena of three segments of 4 MiB refuses a tree of shape 3, about 6 MB, while
//   another one is held, in the middle of placing its tree;
// - refused by their condition: an insert of a key that exists, a replace of a key that does not, a replace
//   with a revision another write has replaced;
// - refused by the validation of the entity at depth, before they reach the engine;
// - withdrawn: a write admitted and waiting behind a commit that the persistence step holds, whose token is
//   cancelled before its dispatch, and which releases what it prepared (HeldPersistence).
// The operations are drawn from a generator with a fixed seed, so a failure repeats; the audit runs after
// every round.
public sealed class OwnershipAfterChurnOfSingleWritesTests
{
    private const int Rounds = 30;

    private const int OperationsPerRound = 12;

    private const int Keys = 8;

    [Fact]
    public async Task Ownership_audit_after_churn_of_single_writes_with_refusals_and_withdrawals_finds_every_block_owned_by_exactly_one_live_entity()
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
                    var outcome = await OperateAsync(items, random, round * OperationsPerRound + operation);

                    outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
                }

                await WithdrawAsync(database, storage, items, random, round);
                OwnershipAudit.Expect(database);
            }

            // Every way a single write ends was met.
            foreach (var outcome in new[] { "written", "deleted", "Memory", "DuplicateKey", "NotFound", "RevisionConflict", "ValidationFailed" })
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
    private static async Task<string> OperateAsync(ILatticeSet items, Random random, int number)
    {
        var key = (long)random.Next(Keys);
        var shape = random.Next(TreeSchema.Shapes);

        try
        {
            switch (random.Next(7))
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
                default:
                    // A null in a List of an owned value, which the validation refuses at depth.
                    var invalid = TreeSchema.RecordOf(items, key, 2, number);
                    invalid.Get<List<LatticeOwnedValue>>("Places")![0].Get<List<string>>("Lines")!.Add(null!);
                    await items.UpsertAsync(invalid);
                    return "written";
            }
        }
        catch (LatticeException exception)
        {
            return exception.Limit is { } limit ? limit.ToString() : exception.Error.ToString();
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
