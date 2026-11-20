namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Refusals;

// A write reserves the nodes of the indexes of its entity type one index after the other, and a
// reservation the arena cannot hold takes nothing: so a write that an index refuses after the indexes
// before it reserved their nodes gives those back as well, and is refused as a write the memory budget
// cannot hold is, before its commit, with no effect (docs/design/ordered-index.md, "The reservation";
// decision on write admission, Q14: WriteRejected with Limit Memory, and the write has had no effect).
// In an arena full but for the room of a root and its texts (FullArena), five fillers are deleted, which
// frees five slots of 1 KiB: an insert into an index of one leaf reserves 2 · 1 + 1 = 3 nodes, so the
// first index of Ranked reserves its nodes and the next takes the two left before it is refused. Second,
// with keys of its own in all three indexes, is refused for memory there: the database holds what it
// held, the nodes of its indexes included (HeldState), a search finds nothing of Second, and the instance
// stays Ready. Once more memory is freed, the same insert succeeds and every index finds it.
public sealed class RefusalAtALaterIndexTests
{
    // Slots of 1 KiB freed: one fewer than two indexes of one leaf reserve for an insert.
    private const int Freed = 5;

    [Fact]
    public async Task Insert_refused_at_a_later_index_gives_back_the_nodes_the_indexes_before_it_reserved_and_succeeds_once_memory_is_freed()
    {
        await using var database = await FullArena.OpenAsync();
        var ranked = database.Set<IndexSchema.Ranked>();

        await FullArena.FreeAsync(database, 1, Freed);
        var held = HeldState.Of(database);

        await FullArena.ExpectRefusedAsync(ranked.InsertAsync(FullArena.Second).AsTask());

        Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
        held.ExpectHeldBy(database);
        Assert.Null(ranked.Get(FullArena.Second.Id));
        RankedSearch.ExpectFound(database, FullArena.Second, []);
        RankedSearch.ExpectFound(database, FullArena.First, [1L]);

        await FullArena.FreeAsync(database, Freed + 1, Freed + 10);
        await ranked.InsertAsync(FullArena.Second);

        RankedSearch.ExpectFound(database, FullArena.Second, [2L]);
        RankedSearch.ExpectFound(database, FullArena.First, [1L]);
    }
}
