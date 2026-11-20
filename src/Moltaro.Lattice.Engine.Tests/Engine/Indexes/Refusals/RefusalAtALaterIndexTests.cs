namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Refusals;

// A write reserves the nodes of the indexes of its entity type one index after the other, and a
// reservation the arena cannot hold takes nothing: so a write that an index refuses after the indexes
// before it reserved their nodes gives those back as well, and is refused as a write the memory budget
// cannot hold is, before its commit, with no effect (docs/design/ordered-index.md, "The reservation";
// decision on write admission, Q14: WriteRejected with Limit Memory, and the write has had no effect).
// The arena is full but for the room of a root and its texts: the fill ends by a refusal of a filler and
// leaves no free slot of the class of the nodes (FullArena). Then one filler after the other is deleted,
// one slot of 1 KiB each, and Second, with keys of its own in all three indexes of Ranked, is written
// after each until it succeeds. At every refusal the database holds what it held before the write, the
// nodes of its indexes included (HeldState), a search finds nothing of Second, and the instance stays
// Ready. An insert takes at least one node in each of the three indexes, so the refusal with one slot
// fewer than the insert succeeds with came after the first index had all it needs: a refusal at a later
// index. Once the insert succeeds, every index finds it.
public sealed class RefusalAtALaterIndexTests
{
    // Far more slots than an insert into three trees of one leaf can take.
    private const int MostFreed = 30;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Insert_refused_at_a_later_index_gives_back_the_nodes_the_indexes_before_it_reserved_and_succeeds_once_memory_is_freed()
    {
        await using var database = await FullArena.OpenAsync();
        var ranked = database.Set<IndexSchema.Ranked>();
        var refusals = 0;

        for (var freed = 1; ; freed++)
        {
            var held = HeldState.Of(database);
            var refusal = await Record.ExceptionAsync(() => ranked.InsertAsync(FullArena.Second).AsTask().WaitAsync(Bound));

            if (refusal is null)
            {
                break;
            }

            var refused = Assert.IsType<LatticeException>(refusal);

            Assert.Equal((LatticeErrorEnum.WriteRejected, LatticeLimitEnum.Memory), (refused.Error, refused.Limit));
            Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
            held.ExpectHeldBy(database);
            Assert.Null(ranked.Get(FullArena.Second.Id));
            RankedSearch.ExpectFound(database, FullArena.Second, []);
            RankedSearch.ExpectFound(database, FullArena.First, [1L]);
            refusals++;

            Assert.True(freed <= MostFreed, $"The insert was refused with {MostFreed} slots of 1 KiB freed.");
            await FullArena.FreeAsync(database, freed, freed);
        }

        Assert.True(refusals > 0, "The insert was never refused.");
        RankedSearch.ExpectFound(database, FullArena.Second, [2L]);
        RankedSearch.ExpectFound(database, FullArena.First, [1L]);
    }
}
