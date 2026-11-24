namespace Moltaro.Lattice.Tests.NestedValues.References;

// A reference stores the key of its target and is never checked: a missing target is not an error
// (LatticeFieldTypeEnum.Reference). It is held as the key of its target, in the
// .NET type of that key, for every type a primary key may have: each integer at its smallest and its largest
// value, a Guid, and a String, short and long, under either comparison of its target, its spelling kept. A
// nullable reference may be null. A reference whose key is text is validated as a String value is, by the
// write: text that is not well-formed is ValidationFailed with the field named (ReferenceSchema).
public sealed class ReferenceTests
{
    // A text longer than one value inline and than one block of its own, so that it lies in chunks.
    private static readonly string LongKey = new('k', 70_000);

    [Fact]
    public async Task Reference_holds_the_key_of_its_target_in_the_type_of_that_key_for_every_key_type()
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

        await links.InsertAsync(smallest);
        await links.InsertAsync(largest);

        foreach (var written in new[] { smallest, largest })
        {
            var read = links.Get((string)written["Code"]!)!;

            foreach (var field in links.EntityType.Fields)
            {
                Assert.Equal(written[field.Name], read[field.Name]);
                Assert.Equal(written[field.Name]?.GetType(), read[field.Name]?.GetType());
            }
        }

        // Through the class, as the key type of the target.
        var link = database.Set<Link>().Get("largest")!;
        Assert.Equal(ulong.MaxValue, link.ToUInt64);
        Assert.Equal(sbyte.MaxValue, link.ToInt8);
        Assert.Equal(LongKey, link.ToText);
        Assert.Equal(LongKey.ToUpperInvariant(), link.ToName);
    }

    [Fact]
    public async Task Null_reference_is_read_back_null_by_name_and_through_the_class()
    {
        await using var database = await TestDatabases.OpenMasterAsync(ReferenceSchema.Schema);
        var links = database.Set<Link>();

        await links.InsertAsync(new Link { Code = "nothing", Required = 0, Guids = [null, Guid.Empty, null] });

        var read = links.Get("nothing")!;
        Assert.Null(read.ToInt8);
        Assert.Null(read.ToUInt64);
        Assert.Null(read.ToGuid);
        Assert.Null(read.ToText);
        Assert.Null(read.ToName);
        Assert.Equal(new Guid?[] { null, Guid.Empty, null }, read.Guids);

        var record = database.Set("Link").Get("nothing")!;
        Assert.All(record.EntityType.Fields.Where(field => field.Name.StartsWith("To", StringComparison.Ordinal)), field => Assert.Null(record[field.Name]));
    }

    [Fact]
    public async Task Reference_to_a_target_that_does_not_exist_is_written_and_kept_after_its_target_is_deleted()
    {
        await using var database = await TestDatabases.OpenMasterAsync(ReferenceSchema.Schema);
        var targets = database.Set("Int64");
        var links = database.Set<Link>();

        // No entity of Int64 has the key 404: the reference is written all the same.
        await links.InsertAsync(new Link { Code = "dangling", Required = 404, ToInt64 = 404, Many = [404, 404] });
        Assert.Null(targets.Get(404L));

        // A target that exists, then is deleted: nothing of the reference changes, and its revision stays.
        await targets.InsertAsync(new LatticeRecord(targets.EntityType) { ["Id"] = 7L });
        await links.InsertAsync(new Link { Code = "to seven", Required = 7, ToInt64 = 7, Many = [7] });
        Assert.True(links.TryGet("to seven", out _, out var before));

        Assert.True((await targets.DeleteAsync(7L)).Changed);

        Assert.True(links.TryGet("to seven", out var kept, out var after));
        Assert.Equal(before, after);
        Assert.Equal(7, kept.Required);
        Assert.Equal(7, kept.ToInt64);
        Assert.Equal([7L], kept.Many);
        Assert.Equal(404, links.Get("dangling")!.ToInt64);
    }

    [Fact]
    public async Task Lists_of_references_keep_their_order_and_their_duplicates_in_each_form()
    {
        await using var database = await TestDatabases.OpenMasterAsync(ReferenceSchema.Schema);
        var links = database.Set<Link>();
        var guid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var written = new Link
        {
            Code = "lists",
            Required = 1,
            Many = [3, 1, 3, long.MinValue, 2],
            Names = ["b", "", LongKey, "b", "B"],
            Guids = [guid, null, guid, Guid.Empty],
        };

        await links.InsertAsync(written);

        var read = links.Get("lists")!;
        Assert.Equal(written.Many, read.Many);
        Assert.Equal(written.Names, read.Names);
        Assert.Equal(written.Guids, read.Guids);

        var record = database.Set("Link").Get("lists")!;
        Assert.Equal(written.Names, record.Get<List<string>>("Names"));
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Reference_whose_key_is_text_that_is_not_well_formed_is_ValidationFailed_with_the_field_named()
    {
        await using var database = await TestDatabases.OpenMasterAsync(ReferenceSchema.Schema);
        var links = database.Set("Link");
        const string LoneSurrogate = "a\uD800b";

        var record = LinkOf(links, "malformed");
        record["ToText"] = LoneSurrogate;

        await TestData.ExpectValidationFailedAsync("ToText", () => links.InsertAsync(record));
        await TestData.ExpectValidationFailedAsync("ToName", () => database.Set<Link>().InsertAsync(new Link { Code = "malformed", ToName = LoneSurrogate }));

        // Also as an element of a List of references, and when added to a write batch.
        var batch = database.CreateWriteBatch();
        TestData.ExpectValidationFailed("Names", () => batch.Insert(new Link { Code = "malformed", Names = ["fine", LoneSurrogate] }));
        Assert.Equal(0, batch.Count);

        Assert.Null(links.Get("malformed"));
    }

    // A record of Link with its key, the reference that is not nullable and its Lists, empty.
    private static LatticeRecord LinkOf(ILatticeSet links, string code) => new(links.EntityType)
    {
        ["Code"] = code,
        ["Required"] = 1L,
        ["Many"] = Array.Empty<long>(),
        ["Names"] = Array.Empty<string>(),
        ["Guids"] = Array.Empty<Guid?>(),
    };
}
