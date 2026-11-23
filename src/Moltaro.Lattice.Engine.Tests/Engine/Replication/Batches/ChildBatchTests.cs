using System.Buffers.Binary;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.Batches.BatchSchema;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Batches;

// A child applies the transaction of a write batch through the publisher of the master and acknowledges only
// its position (decision on the document model: "A child must not expose a prefix or advance applied
// progress past a partial batch"):
// - a batch that inserts, replaces and deletes is applied whole, at its one position, and every member has
//   on the child the revision it has on the master;
// - while its application is held before the position (HeldPublication), the child has acknowledged no
//   sequence of the batch, and once it goes on it acknowledges the position of the batch; so it is with a
//   batch committed through that child, whose commit completes only once the child has applied all of it;
// - an exchange of unique values, which the master checks on the state the batch leaves, is applied, and so
//   is a transaction that exchanges them, or one whose delete frees a value another effect takes, in either
//   order, which the child checks on the state it leaves; a transaction that would give a value of a unique
//   index a second owner on the state it leaves — two effects that claim one value, or an effect that claims
//   the value of an owner no effect frees — stops the child as a protocol failure does, with nothing of it
//   applied: it ends its connection and serves its reads at the position it had, Ready.
// The transactions of the last four are forged by a relay (FrameRelay) from transactions of the master, as
// a master writes the members of a batch: one transaction at the sequence of the first, whose Writes carry
// that sequence.
public sealed class ChildBatchTests
{
    [Fact]
    public async Task Child_applies_a_batch_that_inserts_replaces_and_deletes_whole_at_its_position_with_the_revisions_of_the_master()
    {
        await using var nodes = await BatchNodes.OpenAsync("child");
        var child = nodes.Child!;

        var result = await Members(nodes.Master).CommitAsync(nodes.Session);
        await child.WaitForPositionAsync(nodes.Session).AsTask().WaitAsync(Bound);

        Assert.Equal(nodes.NextSequence, PositionSequence.Of(result.Position));
        Assert.Equal(result.Position, child.Position);

        foreach (var id in Replaced.Concat(Inserted).Concat(Deleted).Append(Other))
        {
            Assert.Equal(After(id), StateOf(child.Set<Item>().Get(id)));

            if (After(id) is not null)
            {
                Assert.True(nodes.Master.Set<Item>().TryGet(id, out _, out var onMaster));
                Assert.True(child.Set<Item>().TryGet(id, out _, out var onChild));
                Assert.Equal(onMaster, onChild);
            }
        }
    }

    [Fact]
    public async Task Child_acknowledges_no_sequence_of_a_batch_before_it_has_applied_all_of_it()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await InsertBeforeAsync(master.Set<Item>());
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var s = PositionSequence.Of(master.Position) + 1;

        using var held = HeldPublication.Arm(child, PublicationPointEnum.BeforePosition);
        var result = await Members(master).CommitAsync();

        await held.ReachedAsync();

        Assert.All(AcknowledgedBy(relay), sequence => Assert.True(sequence < s, $"The child acknowledged {sequence} while it applied the batch at {s}."));

        held.Release();

        await Eventually.HoldsAsync(() => AcknowledgedBy(relay).Contains(s), "The child did not acknowledge the position of the batch.");
        Assert.Equal(result.Position, child.Position);
    }

    [Fact]
    public async Task Child_that_forwarded_a_batch_acknowledges_no_sequence_of_it_and_completes_no_commit_before_it_has_applied_all_of_it()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await InsertBeforeAsync(master.Set<Item>());
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var s = PositionSequence.Of(master.Position) + 1;

        using var held = HeldPublication.Arm(child, PublicationPointEnum.BeforePosition);
        var commit = Members(child).CommitAsync().AsTask();

        await held.ReachedAsync();

        Assert.All(AcknowledgedBy(relay), sequence => Assert.True(sequence < s, $"The child acknowledged {sequence} while it applied the batch at {s}."));
        Assert.False(commit.IsCompleted, "The commit through the child completed while the child applied the batch.");

        held.Release();
        var result = await commit.WaitAsync(Bound);

        Assert.Equal(s, PositionSequence.Of(result.Position));
        Assert.Equal(result.Position, child.Position);
        await Eventually.HoldsAsync(() => AcknowledgedBy(relay).Contains(s), "The child did not acknowledge the position of the batch.");
    }

    [Fact]
    public async Task Child_applies_a_batch_that_exchanges_unique_values_and_goes_on_following()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await InsertBeforeAsync(master.Set<Item>());
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var session = new LatticeSession();

        var swap = master.CreateWriteBatch();
        swap.Replace(new Item { Id = 1, Tag = "old", Group = 1, Login = "login 2" });
        swap.Replace(new Item { Id = 2, Tag = "old", Group = 1, Login = "login 1" });
        await swap.CommitAsync(session);
        await master.Set<Item>().InsertAsync(Of(7, "old", group: 1), session);
        await child.WaitForPositionAsync(session).AsTask().WaitAsync(Bound);

        var items = child.Set<Item>();

        Assert.Equal("login 2", items.Get(1L)!.Login);
        Assert.Equal("login 1", items.Get(2L)!.Login);
        Assert.Equal([2L], items.Index("ByLogin").Find(IndexKey.Equal("login 1")).Select(item => item.Id));
        Assert.NotNull(items.Get(7L));
    }

    // The master inserts 9, moves the login of 1 away and gives it to 7; the relay passes the insert of 9 and
    // that of 7 as one transaction and drops the move: 1 still holds the login.
    [Fact]
    public async Task Child_stops_at_a_transaction_that_takes_the_value_of_an_owner_no_effect_frees_with_nothing_of_it_applied()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await InsertBeforeAsync(master.Set<Item>());
        var (relay, readiness, child) = await ForgedAsync(master, count: 3, kept: [0, 2]);

        await using (relay)
        await using (child)
        {
            var items = master.Set<Item>();

            await items.InsertAsync(new Item { Id = 9, Tag = "old", Group = 1, Login = "nine" });
            await items.ReplaceAsync(new Item { Id = 1, Tag = "old", Group = 1, Login = "moved" });
            await items.InsertAsync(new Item { Id = 7, Tag = "old", Group = 1, Login = "login 1" });
            await relay.ChildEndedAsync();

            ExpectNothingApplied(child, readiness, [9, 7]);
        }
    }

    // The master inserts 7 with a login, deletes it and inserts 8 with the same login; the relay passes the two
    // inserts as one transaction and drops the delete: two effects claim one value.
    [Fact]
    public async Task Child_stops_at_a_transaction_two_effects_of_which_claim_one_value_with_nothing_of_it_applied()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await InsertBeforeAsync(master.Set<Item>());
        var (relay, readiness, child) = await ForgedAsync(master, count: 3, kept: [0, 2]);

        await using (relay)
        await using (child)
        {
            var items = master.Set<Item>();

            await items.InsertAsync(new Item { Id = 7, Tag = "old", Group = 1, Login = "twin" });
            await items.DeleteAsync(7L);
            await items.InsertAsync(new Item { Id = 8, Tag = "old", Group = 1, Login = "twin" });
            await relay.ChildEndedAsync();

            ExpectNothingApplied(child, readiness, [7, 8]);
        }
    }

    // The master parks the login of 1, gives it to 2 and gives the login of 2 to 1; the relay passes the last
    // two as one transaction and drops the first: the transaction exchanges the logins of 1 and 2, which no
    // order of its effects could apply one at a time.
    [Fact]
    public async Task Child_applies_a_transaction_that_exchanges_unique_values_on_the_state_it_leaves()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await InsertBeforeAsync(master.Set<Item>());
        var (relay, readiness, child) = await ForgedAsync(master, count: 3, kept: [1, 2]);

        await using (relay)
        await using (child)
        {
            var items = master.Set<Item>();

            await items.ReplaceAsync(new Item { Id = 1, Tag = "old", Group = 1, Login = "parked" });
            await items.ReplaceAsync(new Item { Id = 2, Tag = "old", Group = 1, Login = "login 1" });
            await items.ReplaceAsync(new Item { Id = 1, Tag = "old", Group = 1, Login = "login 2" });

            var exchanged = PositionSequence.Of(readiness) + 1;

            await Eventually.HoldsAsync(() => PositionSequence.Of(child.Position) == exchanged, "The child did not apply the transaction that exchanges the logins.");

            var onChild = child.Set<Item>();

            Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
            Assert.Equal("login 2", onChild.Get(1L)!.Login);
            Assert.Equal("login 1", onChild.Get(2L)!.Login);
            Assert.Equal([2L], onChild.Index("ByLogin").Find(IndexKey.Equal("login 1")).Select(item => item.Id));
            Assert.Equal([1L], onChild.Index("ByLogin").Find(IndexKey.Equal("login 2")).Select(item => item.Id));
        }
    }

    // The master deletes 1 and gives its login to 9; the relay passes the two as one transaction, the delete
    // first or the insert first: the delete frees the value the insert takes, on the state the transaction
    // leaves, whichever comes first among its effects.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task Child_applies_a_transaction_whose_delete_frees_a_value_another_effect_takes_in_either_order(int first, int second)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
        await InsertBeforeAsync(master.Set<Item>());
        var (relay, readiness, child) = await ForgedAsync(master, count: 2, kept: [first, second]);

        await using (relay)
        await using (child)
        {
            var items = master.Set<Item>();

            await items.DeleteAsync(1L);
            await items.InsertAsync(new Item { Id = 9, Tag = "old", Group = 1, Login = "login 1" });

            var applied = PositionSequence.Of(readiness) + 1;

            await Eventually.HoldsAsync(() => PositionSequence.Of(child.Position) == applied, "The child did not apply the transaction whose delete frees the login.");

            var onChild = child.Set<Item>();

            Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
            Assert.Null(onChild.Get(1L));
            Assert.Equal([9L], onChild.Index("ByLogin").Find(IndexKey.Equal("login 1")).Select(item => item.Id));
        }
    }

    // A child of the master through a relay that holds the next transactions of the master, as many as count,
    // and passes, in their place, one at the sequence of the first with the effects of those kept, in the order
    // given, each Write carrying that sequence as its last mutation; later transactions pass as they are.
    private static async Task<(FrameRelay Relay, LatticePosition Readiness, EngineDatabase Child)> ForgedAsync(EngineDatabase master, int count, int[] kept)
    {
        var held = new List<WireFrame>();
        var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction || held.Count >= count)
            {
                return [frame];
            }

            held.Add(frame);

            return held.Count < count ? [] : [Merged(held, kept)];
        });
        var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        return (relay, child.Position, child);
    }

    private static WireFrame Merged(List<WireFrame> held, int[] kept)
    {
        var sequence = TransactionPayload.SequenceOf(held[0]);
        var effects = new List<byte>();

        foreach (var place in kept)
        {
            var member = TransactionPayload.EffectsOf(held[place]);

            if (member[0] == TransactionPayload.Write)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(member.AsSpan(TransactionPayload.WriteLastMutationAt), sequence);
            }

            effects.AddRange(member);
        }

        return held[0].WithPayload(TransactionPayload.Make(sequence, [.. effects]));
    }

    private static void ExpectNothingApplied(EngineDatabase child, LatticePosition readiness, long[] absent)
    {
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(readiness, child.Position);
        Assert.All(absent, id => Assert.Null(child.Set<Item>().Get(id)));
        Assert.Equal("login 1", child.Set<Item>().Get(1L)!.Login);
    }

    private static List<ulong> AcknowledgedBy(FrameRelay relay) =>
        [.. relay.FromChild.Where(frame => frame.Kind == FrameKindEnum.Applied).Select(TransactionPayload.AppliedOf)];
}
