namespace Moltaro.Lattice.Tests.NestedValues.References;

// An index declared on a reference field finds, by name, the entities whose reference holds a given key, and
// an entity whose reference is null by a search for null, as an index on a field of any other type does. A
// reference is compared as the key of its target, with the comparison of that key (LatticeIndexPart):
// ByToName compares without case, as its target Name does, and ByToText by its code units, also for a key of
// 70,000 characters, which lies in chunks (ReferenceSchema).
public sealed class ReferenceIndexByNameTests
{
    [Fact]
    public async Task Index_on_a_reference_finds_by_name_the_entities_whose_reference_holds_a_key_and_those_whose_reference_is_null()
    {
        await using var database = await TestDatabases.OpenMasterAsync(ReferenceSchema.Schema);
        var links = database.Set("Link");
        var longKey = new string('k', 70_000);

        await links.InsertAsync(LinkOf(links, "a", toInt64: 1L, toText: "ada", toName: "Ada"));
        await links.InsertAsync(LinkOf(links, "b", toInt64: 2L, toText: "ADA", toName: "ADA"));
        await links.InsertAsync(LinkOf(links, "c", toInt64: 1L, toText: null, toName: null));
        await links.InsertAsync(LinkOf(links, "d", toInt64: null, toText: longKey, toName: "grace"));

        Assert.Equal(["a", "c"], CodesOf(links.Index("ByToInt64").Find(IndexKey.Equal(1L))));
        Assert.Equal(["d"], CodesOf(links.Index("ByToInt64").Find(IndexKey.Equal<long?>(null))));
        Assert.Equal(["a", "b"], CodesOf(links.Index("ByToName").Find(IndexKey.Equal("aDA"))));
        Assert.Equal(["a"], CodesOf(links.Index("ByToText").Find(IndexKey.Equal("ada"))));
        Assert.Equal(["d"], CodesOf(links.Index("ByToText").Find(IndexKey.Equal(longKey))));
        Assert.Equal(["c"], CodesOf(links.Index("ByToText").Find(IndexKey.Equal<string?>(null))));
    }

    private static LatticeRecord LinkOf(ILatticeSet links, string code, long? toInt64, string? toText, string? toName) => new(links.EntityType)
    {
        ["Code"] = code,
        ["Required"] = 1L,
        ["Many"] = Array.Empty<long>(),
        ["Names"] = Array.Empty<string>(),
        ["Guids"] = Array.Empty<Guid?>(),
        ["ToInt64"] = toInt64,
        ["ToText"] = toText,
        ["ToName"] = toName,
    };

    private static string[] CodesOf(IEnumerable<LatticeRecord> records) => [.. records.Select(record => (string)record["Code"]!).Order(StringComparer.Ordinal)];
}
