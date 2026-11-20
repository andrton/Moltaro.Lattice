using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Upkeep;

// A null is a value of an index like any other, and in an entry the null bit decides: a null part keeps its
// width and is zero, whatever its root holds under the null (Storage.Ordered.IndexLayout;
// docs/design/ordered-index.md, "The entries of one write"). A root written through the public API holds
// zeros under a null field (Storage.RootWriter clears the fixed part), as it holds zeros for the value 0,
// so a null and a 0 of Value, a nullable Int64 of Optional, are the same stored bytes and only the null bit
// tells them apart. Replaces of one entity, one after the other:
// - from null to 0, and from 0 back to null: the entry moves, a node is retired (RetiredNodes), the walk of
//   ByValue (EngineDatabase.IndexNodes, IndexContents) holds the new key, and a search finds the entity by
//   it and not by the old one;
// - from null to null, with Label, a field in no index, changed: the entry is the same, no node is retired,
//   the walk is as before. Bytes other than zeros under a null come only in a root received from another
//   node; ChildNullUpkeepTests states that case on a child.
public sealed class NullPartUpkeepTests
{
    [Fact]
    public async Task Replace_between_a_null_and_a_zero_whose_stored_bytes_are_the_same_moves_the_entry_by_the_null_bit_alone()
    {
        await using var database = await EngineMaster.OpenAsync(UpkeepSchema.Schema);
        var optional = database.Set("Optional");

        await optional.InsertAsync(OptionalEntities.Of(optional, null, "a"));

        var toZero = await RetiredNodes.OfAsync(database, () => optional.Get(1L) is null ? null : 1L, () => optional.ReplaceAsync(OptionalEntities.Of(optional, 0L, "a")).AsTask(), "Optional", 1);

        Assert.True(toZero > 0, "The replace from null to 0 retired no node of ByValue.");
        Assert.Equal([0L], OptionalEntities.KeysOf(database));
        Assert.Equal([1L], OptionalEntities.FoundBy(optional, 0L));
        Assert.Empty(OptionalEntities.FoundBy(optional, null));

        var toNull = await RetiredNodes.OfAsync(database, () => optional.Get(1L) is null ? null : 1L, () => optional.ReplaceAsync(OptionalEntities.Of(optional, null, "a")).AsTask(), "Optional", 1);

        Assert.True(toNull > 0, "The replace from 0 to null retired no node of ByValue.");
        Assert.Equal([null], OptionalEntities.KeysOf(database));
        Assert.Equal([1L], OptionalEntities.FoundBy(optional, null));
        Assert.Empty(OptionalEntities.FoundBy(optional, 0L));

        var walk = IndexContents.Of(database, "Optional", "ByValue").Walk;
        var nullToNull = await RetiredNodes.OfAsync(database, () => optional.Get(1L) is null ? null : 1L, () => optional.ReplaceAsync(OptionalEntities.Of(optional, null, "b")).AsTask(), "Optional", 1);

        Assert.Equal(0, nullToNull);
        Assert.Equal(walk, IndexContents.Of(database, "Optional", "ByValue").Walk);
        Assert.Equal([1L], OptionalEntities.FoundBy(optional, null));
        Assert.Equal("b", optional.Get(1L)?["Label"]);
    }
}
