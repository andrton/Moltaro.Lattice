using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.Batches.BatchSchema;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Visibility;

// A read that begins at or after the position of a batch sees every member of it in its state after the
// batch, by key, by a search, by a walk of a range and by an enumeration, on the master that publishes it and
// on a child that applies it, and the position of its answer is at that position or later
// (docs/design/write-path.md, "How a publication is made"). The publication of the batch of BatchSchema is
// held right after its position (HeldPublication), while the old entries of the members are still in the
// indexes and their old roots not retired: the members replaced out of the tag "old" and group 1 and the
// members deleted from the tag "doomed" are still named there, and a read passes over them by their roots.
// The reads begin there, once the node's position is at the position of the batch.
public sealed class ReadAtThePositionTests
{
    public static IEnumerable<TheoryDataRow<string>> Nodes => [new("master"), new("child")];

    [Theory]
    [MemberData(nameof(Nodes))]
    public async Task Read_that_begins_at_the_position_of_a_batch_sees_every_member_while_its_old_entries_are_still_in_the_indexes(string node)
    {
        await using var nodes = await BatchNodes.OpenAsync(node);
        var reader = nodes.Reader;
        var items = reader.Set<Item>();
        var s = nodes.NextSequence;

        using var held = HeldPublication.Arm(reader, PublicationPointEnum.AfterPosition);
        var commit = OwnThread.Run(() => OwnThread.Wait(Members(nodes.Master).CommitAsync(nodes.Session)));

        await held.ReachedAsync();

        Assert.True(PositionSequence.Of(reader.Position) >= s, "The position of the node is not that of the batch.");

        foreach (var id in Replaced.Concat(Inserted).Concat(Deleted))
        {
            var answer = await items.GetAsync(id, new LatticeSession());

            Assert.Equal(After(id), StateOf(answer.Entity));
            Assert.True(PositionSequence.Of(answer.Position) >= s, $"The read of {id} is at a position before the batch.");
        }

        ExpectAt(await items.Index("ByTag").FindAsync(IndexKey.Equal("new"), new LatticeFindOptions()), s, InNewTag);
        ExpectAt(await items.Index("ByTag").FindAsync(IndexKey.Equal("new"), new LatticeFindOptions { Limit = 1 }), s, InNewTag[..1]);
        ExpectAt(await items.Index("ByGroup").FindAsync(IndexKey.All.Range(IndexBound.Inclusive(2L), IndexBound.Inclusive(2L)), new LatticeFindOptions()), s, InNewTag);
        ExpectAt(await items.Index("ByTag").FindAsync(IndexKey.Equal("old"), new LatticeFindOptions()), s, [5, 6, Other]);
        ExpectAt(await items.Index("ByGroup").FindAsync(IndexKey.All.Range(IndexBound.Inclusive(1L), IndexBound.Inclusive(1L)), new LatticeFindOptions()), s, [5, 6, Other]);
        ExpectAt(await items.Index("ByTag").FindAsync(IndexKey.Equal("doomed"), new LatticeFindOptions()), s, []);

        Assert.Equal(InNewTag, items.Index("ByTag").Find(IndexKey.Equal("new")).Select(item => item.Id));

        var session = new LatticeSession();
        var enumerated = new List<Item>();

        await foreach (var item in items.EnumerateAsync(session))
        {
            enumerated.Add(item);
        }

        Assert.Equal([1L, 2, 5, 6, 11, 12, Other], enumerated.Select(item => item.Id).Order());
        Assert.All(enumerated, item => Assert.Equal(After(item.Id), StateOf(item)));
        Assert.True(PositionSequence.Of(session.MinPosition!.Value) >= s, "The enumeration is at a position before the batch.");

        held.Release();
        await commit.WaitAsync(Bound);
    }

    // The answer holds the items given, in the order of the index, each in its state after the batch, at the
    // position of the batch or later.
    private static void ExpectAt(LatticeFindResult<Item> answer, ulong s, long[] expected)
    {
        Assert.Equal(expected, answer.Items.Select(item => item.Id));
        Assert.All(answer.Items, item => Assert.Equal(After(item.Id), StateOf(item)));
        Assert.True(PositionSequence.Of(answer.Position) >= s, "A search is at a position before the batch.");
    }
}
