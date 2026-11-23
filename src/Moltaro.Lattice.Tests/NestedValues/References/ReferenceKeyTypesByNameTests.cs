namespace Moltaro.Lattice.Tests.NestedValues.References;

// A reference holds the key of its target in the type of that key, read back by name with that type, for
// every type a primary key may have: the eight integers at their smallest and their largest, Guid, and String
// of either comparison, empty, short and of 70,000 characters, which lies in chunks; null when it is
// nullable and not set; and in Lists of references, packed and as records, in their order and with their
// duplicates (ReferenceSchema). A reference to a target that does not exist is written and kept as it is: a
// reference is never checked, and a missing target is not an error (LatticeFieldTypeEnum.Reference).
public sealed class ReferenceKeyTypesByNameTests
{
    private static readonly string LongKey = new('k', 70_000);

    [Fact]
    public async Task Reference_holds_the_key_of_its_target_in_the_type_of_that_key_for_every_key_type_read_by_name()
    {
        await using var database = await TestDatabases.OpenMasterAsync(ReferenceSchema.Schema);
        var links = database.Set("Link");

        var smallest = LinkOf(links, "smallest");
        smallest["ToInt8"] = sbyte.MinValue;
        smallest["ToUInt8"] = byte.MinValue;
        smallest["ToInt16"] = short.MinValue;
        smallest["ToUInt16"] = ushort.MinValue;
        smallest["ToInt32"] = int.MinValue;
        smallest["ToUInt32"] = uint.MinValue;
        smallest["ToInt64"] = long.MinValue;
        smallest["ToUInt64"] = ulong.MinValue;
        smallest["ToGuid"] = Guid.Empty;
        smallest["ToText"] = "";
        smallest["ToName"] = "Ada";

        var largest = LinkOf(links, "largest");
        largest["ToInt8"] = sbyte.MaxValue;
        largest["ToUInt8"] = byte.MaxValue;
        largest["ToInt16"] = short.MaxValue;
        largest["ToUInt16"] = ushort.MaxValue;
        largest["ToInt32"] = int.MaxValue;
        largest["ToUInt32"] = uint.MaxValue;
        largest["ToInt64"] = long.MaxValue;
        largest["ToUInt64"] = ulong.MaxValue;
        largest["ToGuid"] = Guid.Parse("5f0c1b8e-58a4-4c4e-9a43-2f6f1c0d7e11");
        largest["ToText"] = LongKey;
        largest["ToName"] = LongKey.ToUpperInvariant();

        var unset = LinkOf(links, "unset");

        foreach (var written in new[] { smallest, largest, unset })
        {
            await links.InsertAsync(written);

            var read = links.Get((string)written["Code"]!)!;

            Assert.Equal(NestedSchema.Describe(written), NestedSchema.Describe(read));
            Assert.All(links.EntityType.Fields, field => Assert.Equal(written[field.Name]?.GetType(), read[field.Name]?.GetType()));
        }
    }

    [Fact]
    public async Task Reference_to_a_target_that_does_not_exist_is_written_and_read_back_by_name()
    {
        await using var database = await TestDatabases.OpenMasterAsync(ReferenceSchema.Schema);
        var links = database.Set("Link");
        var dangling = LinkOf(links, "dangling");

        dangling["ToInt64"] = 404L;
        await links.InsertAsync(dangling);

        Assert.Null(database.Set("Int64").Get(404L));
        Assert.Equal(404L, links.Get("dangling")!["ToInt64"]);
    }

    // A link with its required reference and its Lists of references, its nullable references not set.
    private static LatticeRecord LinkOf(ILatticeSet links, string code) => new(links.EntityType)
    {
        ["Code"] = code,
        ["Required"] = 1L,
        ["Many"] = new long[] { 3, 1, 3, long.MinValue, 2 },
        ["Names"] = new[] { "b", "", LongKey, "b", "B" },
        ["Guids"] = new Guid?[] { Guid.Empty, null, Guid.Empty },
    };
}
