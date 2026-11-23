using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Tests.ReadsAndWrites.Batches;

// The identities of a write batch are claimed together, so batches that share entities never wait for each
// other in a cycle, and the writes of one entity keep the order of their calls whether they are single writes
// or members of a batch (docs/design/write-path.md, "What an application can rely on"). Two callers commit
// batches of the same two entities in opposite orders, many times at once: every commit completes, each at a
// position of its own, and the two entities end with the state of the same batch, the one at the latest
// position. A single write, a batch and a single write of one entity, called one after another without
// awaiting, are executed in that order.
public sealed class BatchOrderTests
{
    private const int Rounds = 200;

    [Fact]
    public async Task Batches_of_the_same_entities_in_opposite_orders_never_wait_for_each_other_and_end_with_the_state_of_the_latest()
    {
        await using var database = await TestData.OpenMasterAsync();
        var users = database.Set<User>();

        async Task<List<(ulong Sequence, string Name)>> CommitAsync(string caller, bool reversed)
        {
            var committed = new List<(ulong, string)>();

            for (var round = 0; round < Rounds; round++)
            {
                var name = $"{caller} {round}";
                var batch = database.CreateWriteBatch();
                var first = new User { Id = reversed ? 2 : 1, Name = name };
                var second = new User { Id = reversed ? 1 : 2, Name = name };

                batch.Upsert(first);
                batch.Upsert(second);

                var result = await batch.CommitAsync();

                committed.Add((PositionSequence.Of(result.Position), name));
            }

            return committed;
        }

        var forward = Task.Run(() => CommitAsync("forward", reversed: false));
        var backward = Task.Run(() => CommitAsync("backward", reversed: true));
        var all = (await Task.WhenAll(forward, backward).WaitAsync(TestWait.Bound)).SelectMany(committed => committed).ToList();

        Assert.Equal(2 * Rounds, all.Select(commit => commit.Sequence).Distinct().Count());

        var latest = all.MaxBy(commit => commit.Sequence).Name;

        Assert.Equal(latest, users.Get(1L)!.Name);
        Assert.Equal(latest, users.Get(2L)!.Name);
    }

    [Fact]
    public async Task Single_write_batch_and_single_write_of_one_entity_are_executed_in_the_order_of_their_calls()
    {
        await using var database = await TestData.OpenMasterAsync();
        var users = database.Set<User>();

        var batch = database.CreateWriteBatch();
        batch.Upsert(new User { Id = 1, Name = "batch" });
        batch.Upsert(new Tag { Name = "admin", Color = "red" });

        var single = users.UpsertAsync(new User { Id = 1, Name = "first" }).AsTask();
        var batched = batch.CommitAsync().AsTask();
        var last = users.UpsertAsync(new User { Id = 1, Name = "last" }).AsTask();
        var results = await Task.WhenAll(single, batched, last).WaitAsync(TestWait.Bound);

        Assert.True(PositionSequence.Of(results[0].Position) < PositionSequence.Of(results[1].Position));
        Assert.True(PositionSequence.Of(results[1].Position) < PositionSequence.Of(results[2].Position));
        Assert.Equal("last", users.Get(1L)!.Name);
        Assert.NotNull(database.Set<Tag>().Get("admin"));
    }
}
