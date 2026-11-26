namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Unique;

// A write refused for a value of a unique index has no effect and leaves nothing behind (decision on
// unique indexes, "It changes no application data"; decision on write admission, Q14): its key is checked
// before any index reserves a node, in the same hold, so it takes nothing from the pool of any index, and
// the root it encoded is freed. After a drain the database
// holds what it held before (HeldState): every counter, the prepared bytes of the arena among them, which
// are the pools of the indexes, every text entry and every node. For comparison, an insert that is
// accepted and then deleted leaves the prepared bytes as they were as well: after a publication the pool
// of an index holds the standing reserve of its tree less the nodes the publication took, which the next
// preparation tops up (docs/design/ordered-index.md, "The standing reserve"), and the tree has the shape
// it had. An insert, a replace that moves its key onto the value
// and an upsert; Account of UniqueSchema, with a login longer than a copy in an entry, so that a refused
// write adds a text entry and frees it.
public sealed class RefusedUniqueWriteTests
{
    [Fact]
    public async Task Refused_write_leaves_every_counter_every_pool_every_text_and_every_node_as_it_was()
    {
        await using var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { DisableBackgroundMaintenance = true });
        var accounts = database.Set<UniqueSchema.Account>();
        var taken = new string('v', 100);

        await accounts.InsertAsync(UniqueSchema.Of(1, taken));
        await accounts.InsertAsync(UniqueSchema.Of(2, "grace", note: "admiral"));

        var held = HeldState.Of(database);
        var prepared = database.Counters.Arena.PreparedBytes;

        Assert.True(prepared > 0, "The premise: the pools of the index hold a standing reserve.");

        await UniqueSchema.ExpectTakenAsync("ByLogin", accounts.InsertAsync(UniqueSchema.Of(3, taken, data: 1000)).AsTask());
        await UniqueSchema.ExpectTakenAsync("ByLogin", accounts.ReplaceAsync(UniqueSchema.Of(2, taken, note: "moved")).AsTask());
        await UniqueSchema.ExpectTakenAsync("ByLogin", accounts.UpsertAsync(UniqueSchema.Of(4, taken)).AsTask());

        held.ExpectHeldBy(database);
        Assert.Null(accounts.Get(3L));
        Assert.Equal("grace", accounts.Get(2L)!.Login);
        Assert.Equal("admiral", accounts.Get(2L)!.Note);

        // An accepted insert of a new value and its delete, for comparison.
        await accounts.InsertAsync(UniqueSchema.Of(5, new string('w', 100)));
        await accounts.DeleteAsync(5L);
        database.DrainRetired();

        Assert.Equal(prepared, database.Counters.Arena.PreparedBytes);

        // The publication of the delete took the one leaf it copied from the pool, and the next
        // preparation tops the pool up (docs/design/ordered-index.md, "The standing reserve").
        Assert.Equal(IndexReserve.BytesOf(database, "Account") - IndexSchema.NodeBytes, database.Counters.Arena.PreparedBytes);
    }
}
