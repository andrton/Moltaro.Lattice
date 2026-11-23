namespace Moltaro.Lattice.Tests.NestedValues.References;

// An index declared on a reference field finds the entities whose reference holds a given key, and an entity
// whose reference is null is found by a search for null, as for an index on a field of any other type. A
// reference is compared as the key of its target, with the comparison of that key (LatticeIndexPart):
// ByToName compares without case, as its target Name does, and ByToText by its code units (ReferenceSchema).
public sealed class ReferenceIndexTests
{
    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Index_on_a_reference_finds_the_entities_whose_reference_holds_a_key_and_those_whose_reference_is_null()
    {
        await using var database = await TestDatabases.OpenMasterAsync(ReferenceSchema.Schema);
        var links = database.Set<Link>();

        await links.InsertAsync(new Link { Code = "a", ToInt64 = 1, ToText = "ada", ToName = "Ada" });
        await links.InsertAsync(new Link { Code = "b", ToInt64 = 2, ToText = "ADA", ToName = "ADA" });
        await links.InsertAsync(new Link { Code = "c", ToInt64 = 1, ToText = null, ToName = null });
        await links.InsertAsync(new Link { Code = "d", ToInt64 = null, ToText = "grace", ToName = "grace" });

        Assert.Equal(["a", "c"], CodesOf(links.Index("ByToInt64").Find(IndexKey.Equal(1L))));
        Assert.Equal(["d"], CodesOf(links.Index("ByToInt64").Find(IndexKey.Equal<long?>(null))));
        Assert.Equal(["a"], CodesOf(links.Index("ByToText").Find(IndexKey.Equal("ada"))));
        Assert.Equal(["c"], CodesOf(links.Index("ByToText").Find(IndexKey.Equal<string?>(null))));
        Assert.Equal(["a", "b"], CodesOf(links.Index("ByToName").Find(IndexKey.Equal("aDa"))));
        Assert.Equal(["c"], CodesOf(links.Index("ByToName").Find(IndexKey.Equal<string?>(null))));

        // A reference that changes moves its entity in the index, by name as through the class.
        var byName = database.Set("Link");
        var record = byName.Get("c")!;
        record["ToInt64"] = 2L;
        await byName.ReplaceAsync(record);

        Assert.Equal(["a"], CodesOf(links.Index("ByToInt64").Find(IndexKey.Equal(1L))));
        Assert.Equal(["b", "c"], CodesOf(links.Index("ByToInt64").Find(IndexKey.Equal(2L))));
    }

    private static string[] CodesOf(IEnumerable<Link> found) => [.. found.Select(link => link.Code).Order(StringComparer.Ordinal)];
}
