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

// A child shows no effect of a transaction of several effects in its new state before the position of the
// transaction while it applies it, and every effect to a read that begins at or after that position, as a
// master does for a batch it publishes (docs/design/arena-protocol.md, "The write batch extension"; decision
// on the document model, "Atomic WriteBatch within one database"). The relay (FrameRelay) holds the six
// transactions of the single writes the master makes for the members of the batch of BatchSchema, in their
// order, and passes in their place one transaction at the sequence s of the first, whose Writes carry s, as a
// master writes the members of a batch. The child's publication of it is held at a place between its steps
// (HeldPublication), and readers on the child, each provably inside the engine before the publication goes
// on (the read switch):
// - a read by key of a member returns its state after the transaction at s or later, its state before it
//   before s; a read of a member that waits returns once the publication goes on;
// - a search of the new tag and a walk of the range of group 2 find no member before s, and at s or later
//   some of the members in their state after the transaction, in the order of the index, since a search
//   that runs across the publication may return some members as they were before it and others as after;
//   begun once the child has applied the transaction, they find every member; a lazy walk of every group
//   that returned entities before the transaction returns none twice;
// - an enumeration with a session returns each member in its state after the transaction once the session
//   is at s or later;
// - a read that waits for a publication that fails ends with Unavailable, and an asynchronous one ends with
//   its token while the publication is held.
// A read of the item that is no member returns at once while the publication is held.
public sealed class ChildPublicationTests
{
    private const int Members = 6;

    public static IEnumerable<TheoryDataRow<string>> Places =>
    [
        new(nameof(PublicationPointEnum.AfterNewEntries)),
        new(nameof(PublicationPointEnum.AfterFirstChange)),
        new(nameof(PublicationPointEnum.BeforePosition)),
        new(nameof(PublicationPointEnum.AfterPosition)),
    ];

    [Theory]
    [MemberData(nameof(Places))]
    public async Task Child_that_applies_a_transaction_held_at_a_place_shows_no_member_in_its_new_state_before_its_position_and_every_member_once_a_read_begins_after(string place)
    {
        await using var nodes = await ForgedNodes.OpenAsync();
        var items = nodes.Child.Set<Item>();

        using var held = HeldPublication.Arm(nodes.Child, Enum.Parse<PublicationPointEnum>(place));
        await nodes.WriteMembersAsync();
        await held.ReachedAsync();

        var byKey = new Dictionary<long, Task<LatticeReadResult<Item>>>();

        foreach (var id in Replaced.Concat(Inserted).Concat(Deleted))
        {
            byKey[id] = await StartAsync(nodes.Child, () => OwnThread.Wait(items.GetAsync(id, new LatticeSession())));
        }

        var newTag = await StartAsync(nodes.Child, () => OwnThread.Wait(items.Index("ByTag").FindAsync(IndexKey.Equal("new"), new LatticeFindOptions())));
        var newGroup = await StartAsync(nodes.Child, () => OwnThread.Wait(items.Index("ByGroup").FindAsync(IndexKey.All.Range(IndexBound.Inclusive(2L), IndexBound.Inclusive(2L)), new LatticeFindOptions())));
        var enumeration = OwnThread.Run(() => Enumerate(items));

        Assert.Equal(("old", 1L), StateOf(items.Get(Other)));

        held.Release();
        await nodes.AppliedAsync();

        foreach (var (id, read) in byKey)
        {
            var answer = await read.WaitAsync(Bound);

            Assert.Equal(PositionSequence.Of(answer.Position) >= nodes.S ? After(id) : Before(id), StateOf(answer.Entity));
        }

        ExpectNoMemberBeforeThePosition(await newTag.WaitAsync(Bound), nodes.S, InNewTag);
        ExpectNoMemberBeforeThePosition(await newGroup.WaitAsync(Bound), nodes.S, InNewTag);

        foreach (var (item, sequence) in await enumeration.WaitAsync(Bound))
        {
            Assert.Equal(sequence >= nodes.S ? After(item.Id) : Before(item.Id), StateOf(item));
        }

        // A search and a walk that begin once the child has applied the transaction.
        Assert.Equal(InNewTag, (await items.Index("ByTag").FindAsync(IndexKey.Equal("new"), new LatticeFindOptions())).Items.Select(item => item.Id));
        Assert.Equal(InNewTag, (await items.Index("ByGroup").FindAsync(IndexKey.All.Range(IndexBound.Inclusive(2L), IndexBound.Inclusive(2L)), new LatticeFindOptions())).Items.Select(item => item.Id));
    }

    [Fact]
    public async Task Read_of_a_member_on_a_child_waits_for_the_position_of_its_transaction_and_returns_its_state_after_it()
    {
        await using var nodes = await ForgedNodes.OpenAsync();
        var items = nodes.Child.Set<Item>();

        using var held = HeldPublication.Arm(nodes.Child, PublicationPointEnum.BeforePosition);
        await nodes.WriteMembersAsync();
        await held.ReachedAsync();

        var read = await StartAsync(nodes.Child, () => OwnThread.Wait(items.GetAsync(1L, new LatticeSession())));

        Assert.False(read.IsCompleted, "A read of a member returned while the position of its transaction was not published.");
        Assert.Equal(("old", 1L), StateOf(items.Get(Other)));

        held.Release();

        var answer = await read.WaitAsync(Bound);

        Assert.Equal(After(1), StateOf(answer.Entity));
        Assert.True(PositionSequence.Of(answer.Position) >= nodes.S);
    }

    [Fact]
    public async Task Read_that_waits_for_a_publication_of_a_child_that_fails_ends_with_Unavailable()
    {
        await using var nodes = await ForgedNodes.OpenAsync();
        var items = nodes.Child.Set<Item>();

        using var held = HeldPublication.Arm(nodes.Child, PublicationPointEnum.BeforePosition);
        await nodes.WriteMembersAsync();
        await held.ReachedAsync();

        var byKey = await StartAsync(nodes.Child, () => items.Get(2L));
        var search = await StartAsync(nodes.Child, () => OwnThread.Wait(items.Index("ByTag").FindAsync(IndexKey.Equal("new"), new LatticeFindOptions())));

        held.Fail();

        Assert.Equal(LatticeErrorEnum.Unavailable, (await Assert.ThrowsAsync<LatticeException>(() => byKey.WaitAsync(Bound))).Error);
        Assert.Equal(LatticeErrorEnum.Unavailable, (await Assert.ThrowsAsync<LatticeException>(() => search.WaitAsync(Bound))).Error);
        Assert.Equal(LatticeDatabaseStateEnum.Faulted, nodes.Child.State);
    }

    [Fact]
    public async Task Asynchronous_read_that_waits_for_the_position_of_a_transaction_of_a_child_ends_with_its_token()
    {
        await using var nodes = await ForgedNodes.OpenAsync();
        var items = nodes.Child.Set<Item>();

        using var held = HeldPublication.Arm(nodes.Child, PublicationPointEnum.BeforePosition);
        await nodes.WriteMembersAsync();
        await held.ReachedAsync();

        using var cancellation = new CancellationTokenSource();
        var byKey = await StartAsync(nodes.Child, () => OwnThread.Wait(items.GetAsync(1L, new LatticeSession(), cancellation.Token)));
        var search = await StartAsync(nodes.Child, () => OwnThread.Wait(items.Index("ByTag").FindAsync(IndexKey.Equal("new"), new LatticeFindOptions(), cancellation.Token)));

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => byKey.WaitAsync(Bound));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(Bound));

        held.Release();
        await nodes.AppliedAsync();

        Assert.Equal(After(1), StateOf(items.Get(1L)));
    }

    // The walk of every group, lazy, has returned items before the transaction; it goes on while the
    // publication stands after the stores of its first member, a step of it waits there for the position and
    // goes on, and no item is returned twice.
    [Fact]
    public async Task Lazy_walk_that_returned_items_before_a_transaction_and_waits_in_it_returns_no_item_twice()
    {
        await using var nodes = await ForgedNodes.OpenAsync();
        var items = nodes.Child.Set<Item>();
        var walk = items.Index("ByGroup").Find(IndexKey.All.Range(IndexBound.Inclusive(1L), IndexBound.Inclusive(2L))).GetEnumerator();
        var returned = new List<long>();

        Assert.True(walk.MoveNext());
        returned.Add(walk.Current.Id);

        using var held = HeldPublication.Arm(nodes.Child, PublicationPointEnum.AfterFirstChange);
        await nodes.WriteMembersAsync();
        await held.ReachedAsync();

        var rest = await StartAsync(nodes.Child, () =>
        {
            var more = new List<long>();

            while (walk.MoveNext())
            {
                more.Add(walk.Current.Id);
            }

            return more;
        });

        held.Release();
        await nodes.AppliedAsync();

        returned.AddRange(await rest.WaitAsync(Bound));
        walk.Dispose();

        Assert.Equal(returned.Count, returned.Distinct().Count());
        Assert.Contains(Other, returned);
    }

    // Starts the read on a thread of its own and returns once it has taken its first root inside the engine,
    // or ended: the read switch tells the first.
    private static async Task<Task<T>> StartAsync<T>(EngineDatabase database, Func<T> read)
    {
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        database.ArmReadSwitch(_ => inside.TrySetResult());

        var started = OwnThread.Run(read);

        await Task.WhenAny(inside.Task, started).WaitAsync(Bound);

        return started;
    }

    // Each item of an enumeration with a session, with the sequence of the session once it has the item.
    private static List<(Item Item, ulong Sequence)> Enumerate(ILatticeSet<Item> items)
    {
        var session = new LatticeSession();
        var seen = new List<(Item, ulong)>();
        var enumerator = items.EnumerateAsync(session).GetAsyncEnumerator();

        try
        {
            while (OwnThread.Wait(enumerator.MoveNextAsync()))
            {
                seen.Add((enumerator.Current, PositionSequence.Of(session.MinPosition!.Value)));
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return seen;
    }

    // A master that holds the state before the batch of BatchSchema and a child of it through a relay that
    // holds the next six transactions and passes, in their place, one at the sequence s of the first with
    // the effects of all of them, each Write carrying s.
    private sealed class ForgedNodes : IAsyncDisposable
    {
        private readonly FrameRelay _relay;

        private ForgedNodes(EngineDatabase master, FrameRelay relay, EngineDatabase child)
        {
            Master = master;
            _relay = relay;
            Child = child;
            S = PositionSequence.Of(child.Position) + 1;
        }

        public EngineDatabase Master { get; }

        public EngineDatabase Child { get; }

        public ulong S { get; }

        public static async Task<ForgedNodes> OpenAsync()
        {
            var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);
            await InsertBeforeAsync(master.Set<Item>());

            var held = new List<WireFrame>();
            var relay = FrameRelay.Start(master.ListenUri!, frame =>
            {
                if (frame.Kind != FrameKindEnum.Transaction || held.Count >= Members)
                {
                    return [frame];
                }

                held.Add(frame);

                return held.Count < Members ? [] : [Merged(held)];
            });
            var child = await ReplicationNodes.OpenChildAsync(relay.Address);

            await child.WaitForPositionAsync(master.Position, Bound);

            return new ForgedNodes(master, relay, child);
        }

        // The single writes of the members of the batch of BatchSchema, in their order, on the master.
        public async Task WriteMembersAsync()
        {
            var items = Master.Set<Item>();

            await items.ReplaceAsync(Of(2, "new", group: 2));
            await items.InsertAsync(Of(11, "new", group: 2));
            await items.DeleteAsync(4L);
            await items.ReplaceAsync(Of(1, "new", group: 2));
            await items.InsertAsync(Of(12, "new", group: 2));
            await items.DeleteAsync(3L);
        }

        // Completes once the child has applied the forged transaction.
        public Task AppliedAsync() =>
            Eventually.HoldsAsync(() => PositionSequence.Of(Child.Position) >= S, "The child did not apply the forged transaction.");

        public async ValueTask DisposeAsync()
        {
            await Child.DisposeAsync();
            await _relay.DisposeAsync();
            await Master.DisposeAsync();
        }

        private static WireFrame Merged(List<WireFrame> held)
        {
            var sequence = TransactionPayload.SequenceOf(held[0]);
            var effects = new List<byte>();

            foreach (var frame in held)
            {
                var member = TransactionPayload.EffectsOf(frame);

                if (member[0] == TransactionPayload.Write)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(member.AsSpan(TransactionPayload.WriteLastMutationAt), sequence);
                }

                effects.AddRange(member);
            }

            return held[0].WithPayload(TransactionPayload.Make(sequence, [.. effects]));
        }
    }
}
