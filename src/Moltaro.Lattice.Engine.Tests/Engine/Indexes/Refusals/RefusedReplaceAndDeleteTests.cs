namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Refusals;

// A replace that changes a key and a delete of an entity with indexes take nodes of its indexes, as an
// insert does: each copies the nodes it changes into nodes reserved before the commit. A reservation the
// arena cannot hold takes nothing and is refused as a write the memory budget cannot hold is: before its
// commit (docs/design/ordered-index.md, "The reservation"; decision on write admission, Q14: WriteRejected
// with Limit Memory, and the write has had no effect). In an arena full but for the room of a root and its
// texts, whose fill ends by a refusal of a filler and leaves no free slot of the class of the nodes
// (FullArena), a replace of the first entity of Ranked that gives it new keys in all three indexes,
// a new long Login and a new Region among them, and a delete of it, are each refused for memory: the
// entity keeps its state, a search of each index finds it by the keys it had and nothing by the keys of
// the replace, the database holds what it held, its text entries and the nodes of its indexes included
// (HeldState), and the instance stays Ready. Once memory is freed, the same write succeeds.
public sealed class RefusedReplaceAndDeleteTests
{
    [Fact]
    public async Task Replace_whose_index_nodes_the_arena_cannot_hold_is_refused_for_memory_keeps_the_entity_and_its_entries_and_succeeds_once_memory_is_freed()
    {
        await using var database = await FullArena.OpenAsync();
        var ranked = database.Set<IndexSchema.Ranked>();

        // The first entity, given the keys of Second.
        var replacement = FullArena.Second;
        replacement.Id = FullArena.First.Id;

        FullArena.ExpectFull(database);
        var held = HeldState.Of(database);

        await FullArena.ExpectRefusedAsync(ranked.ReplaceAsync(replacement).AsTask());

        Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
        held.ExpectHeldBy(database);
        Assert.Equal(FullArena.First.Number, ranked.Get(1L)?.Number);
        RankedSearch.ExpectFound(database, FullArena.First, [1L]);
        RankedSearch.ExpectFound(database, replacement, []);

        // Thirty slots of 1 KiB, more than the replace reserves: a move in each of three trees of one leaf
        // reserves an insert and a remove, 3 + 3 nodes.
        await FullArena.FreeAsync(database, 1, 30);
        await ranked.ReplaceAsync(replacement);

        Assert.Equal(replacement.Number, ranked.Get(1L)?.Number);
        RankedSearch.ExpectFound(database, FullArena.First, []);
        RankedSearch.ExpectFound(database, replacement, [1L]);
    }

    [Fact]
    public async Task Delete_whose_index_nodes_the_arena_cannot_hold_is_refused_for_memory_keeps_the_entity_and_its_entries_and_succeeds_once_memory_is_freed()
    {
        await using var database = await FullArena.OpenAsync();
        var ranked = database.Set<IndexSchema.Ranked>();

        FullArena.ExpectFull(database);
        var held = HeldState.Of(database);

        await FullArena.ExpectRefusedAsync(ranked.DeleteAsync(1L).AsTask());

        Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
        held.ExpectHeldBy(database);
        Assert.NotNull(ranked.Get(1L));
        RankedSearch.ExpectFound(database, FullArena.First, [1L]);

        await FullArena.FreeAsync(database, 1, 10);
        await ranked.DeleteAsync(1L);

        Assert.Null(ranked.Get(1L));
        RankedSearch.ExpectFound(database, FullArena.First, []);
    }
}
