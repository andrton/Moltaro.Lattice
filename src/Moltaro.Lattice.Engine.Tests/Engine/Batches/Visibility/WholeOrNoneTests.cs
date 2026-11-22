using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.Batches.BatchSchema;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Visibility;

// A write batch is exposed whole or not at all, by key, by a search and by an enumeration, on the master
// that publishes it and on a child that applies it, and no answer carries its position before all of it is
// visible (the brief of Epic 1, sprint S17, Acceptance and point 2; decision on the document model, "Atomic
// WriteBatch within one database", as the owner's answer to Q57 has it). The publication of the batch of
// BatchSchema is held at each place between its steps (HeldPublication): on the master the publication of
// its commit, on a child the application of its transaction. While it is held, readers start, each on a
// thread of its own and each provably inside the engine before the publication goes on (the read switch);
// then the publication goes on, and every answer is held against s, the position of the batch:
// - a read by key returns the state after the batch with a position at s or later, and the state before it
//   with a position before s, an absent key included: an item the batch inserts is absent only before s,
//   one it deletes only at s or later;
// - a read of the first changed member that finds its new state is followed by a read of another member
//   that finds its new state too;
// - a search of the tag the batch gives, and a walk of the range of its group, find no item before s and
//   every member after it, and a search for the first entity of that tag finds the first in the order of
//   the index; a search of the tag of the deleted members that ends with nothing ends at s or later, and
//   one that ends before s finds both;
// - an enumeration with a session returns each member in its state after the batch once the session is at
//   s or later, and in its state before it while the session is before s.
// A read of the item that is no member returns at once while the publication is held.
//
// A step of a search may combine what it found before the position with what it found after it only across
// steps, as separate reads may (decision on the document model): the search for the deleted members states
// only what a step cannot see.
public sealed class WholeOrNoneTests
{
    public static IEnumerable<TheoryDataRow<string, string>> Cases =>
        from node in new[] { "master", "child" }
        from point in new[]
        {
            nameof(PublicationPointEnum.AfterNewEntries),
            nameof(PublicationPointEnum.AfterFirstChange),
            nameof(PublicationPointEnum.BeforePosition),
            nameof(PublicationPointEnum.AfterPosition),
        }
        select new TheoryDataRow<string, string>(node, point);

    [Theory(Skip = SkipReason.WriteBatch)]
    [MemberData(nameof(Cases))]
    public async Task Reader_during_the_publication_of_a_batch_sees_all_of_it_or_none_and_no_position_before_it_is_visible(string node, string point)
    {
        await using var nodes = await BatchNodes.OpenAsync(node);
        var reader = nodes.Reader;
        var items = reader.Set<Item>();
        var s = nodes.NextSequence;

        using var held = HeldPublication.Arm(reader, Enum.Parse<PublicationPointEnum>(point));
        var commit = OwnThread.Run(() => OwnThread.Wait(Members(nodes.Master).CommitAsync(nodes.Session)));

        await held.ReachedAsync();

        var enumeration = OwnThread.Run(() => Enumerate(items));
        var byKey = new Dictionary<long, Task<LatticeReadResult<Item>>>();

        foreach (var id in Replaced.Concat(Inserted).Concat(Deleted))
        {
            byKey[id] = await StartAsync(reader, () => OwnThread.Wait(items.GetAsync(id, new LatticeSession())));
        }

        var pair = await StartAsync(reader, () => (OwnThread.Wait(items.GetAsync(2L, new LatticeSession())), OwnThread.Wait(items.GetAsync(1L, new LatticeSession()))));
        var newTag = await StartAsync(reader, () => OwnThread.Wait(items.Index("ByTag").FindAsync(IndexKey.Equal("new"), new LatticeFindOptions())));
        var firstOfNewTag = await StartAsync(reader, () => OwnThread.Wait(items.Index("ByTag").FindAsync(IndexKey.Equal("new"), new LatticeFindOptions { Limit = 1 })));
        var newGroup = await StartAsync(reader, () => OwnThread.Wait(items.Index("ByGroup").FindAsync(IndexKey.All.Range(IndexBound.Inclusive(2L), IndexBound.Inclusive(2L)), new LatticeFindOptions())));
        var doomed = await StartAsync(reader, () => OwnThread.Wait(items.Index("ByTag").FindAsync(IndexKey.Equal("doomed"), new LatticeFindOptions())));

        // The item that is no member, while the publication is held.
        Assert.Equal(("old", 1L), StateOf(items.Get(Other)));

        held.Release();
        await commit.WaitAsync(Bound);
        await reader.WaitForPositionAsync(nodes.Session, CancellationToken.None).AsTask().WaitAsync(Bound);

        foreach (var (id, read) in byKey)
        {
            var answer = await read.WaitAsync(Bound);

            Assert.Equal(PositionSequence.Of(answer.Position) >= s ? After(id) : Before(id), StateOf(answer.Entity));
        }

        var (first, second) = await pair.WaitAsync(Bound);

        if (Equals(StateOf(first.Entity), After(2)))
        {
            Assert.Equal(After(1), StateOf(second.Entity));
        }

        ExpectNoneOrAll(await newTag.WaitAsync(Bound), s, InNewTag);
        ExpectNoneOrAll(await firstOfNewTag.WaitAsync(Bound), s, InNewTag[..1]);
        ExpectNoneOrAll(await newGroup.WaitAsync(Bound), s, InNewTag);

        var deleted = await doomed.WaitAsync(Bound);

        if (deleted.Items.Count == 0)
        {
            Assert.True(PositionSequence.Of(deleted.Position) >= s, "A search ended with none of the deleted members before their delete.");
        }

        if (PositionSequence.Of(deleted.Position) < s)
        {
            Assert.Equal([3L, 4L], deleted.Items.Select(item => item.Id).Order());
        }

        foreach (var (item, sequence) in await enumeration.WaitAsync(Bound))
        {
            Assert.Equal(sequence >= s ? After(item.Id) : Before(item.Id), StateOf(item));
        }
    }

    // The answer of a search before s finds none of the members, and at s or later every one, in the order
    // of the index.
    private static void ExpectNoneOrAll(LatticeFindResult<Item> answer, ulong s, long[] members)
    {
        var found = answer.Items.Select(item => item.Id).ToArray();

        if (PositionSequence.Of(answer.Position) < s)
        {
            Assert.Empty(found);
        }
        else
        {
            Assert.Equal(members, found);
            Assert.All(answer.Items, item => Assert.Equal(After(item.Id), StateOf(item)));
        }
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
            OwnThread.Wait(DisposedAsync(enumerator));
        }

        return seen;
    }

    private static async ValueTask<bool> DisposedAsync(IAsyncEnumerator<Item> enumerator)
    {
        await enumerator.DisposeAsync();

        return true;
    }
}
