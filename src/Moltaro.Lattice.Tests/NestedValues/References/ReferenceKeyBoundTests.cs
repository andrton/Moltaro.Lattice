using Moltaro.Lattice.TestSupport.Values;

namespace Moltaro.Lattice.Tests.NestedValues.References;

// A reference whose target has a String key holds text, and the bound of one String value holds for it as for
// a String (LatticeFieldTypeEnum.Reference): text above 268,173,376 bytes in UTF-8, the text of ValueBound, is
// ValidationFailed with the field named, by the write and before its conditions, for a reference of the
// entity (ToText) and as an element of a List of references (Names), through the class and by name; text of
// exactly the bound passes, and the write reaches its condition (ReferenceSchema). The class runs alone
// (LargeValueCollection).
[Collection<LargeValueCollection>]
public sealed class ReferenceKeyBoundTests
{
    [Fact]
    public async Task Reference_text_above_the_bound_of_one_value_is_ValidationFailed_at_the_top_and_as_an_element()
    {
        await using var database = await TestDatabases.OpenMasterAsync(ReferenceSchema.Schema);
        var byClass = database.Set<Link>();
        var byName = database.Set("Link");
        var text = ValueBound.TextOneByteAboveInUtf8;

        await TestData.ExpectValidationFailedAsync("ToText", () => byClass.ReplaceAsync(new Link { Code = "absent", ToText = text }));
        await TestData.ExpectValidationFailedAsync("Names", () => byClass.ReplaceAsync(new Link { Code = "absent", Names = ["a", text] }));

        var record = new LatticeRecord(byName.EntityType)
        {
            ["Code"] = "absent",
            ["Required"] = 1L,
            ["Many"] = Array.Empty<long>(),
            ["Names"] = new[] { text },
            ["Guids"] = Array.Empty<Guid?>(),
        };

        await TestData.ExpectValidationFailedAsync("Names", () => byName.ReplaceAsync(record));

        record["Names"] = Array.Empty<string>();
        record["ToName"] = text;
        await TestData.ExpectValidationFailedAsync("ToName", () => byName.ReplaceAsync(record));

        // Exactly the bound: the key is absent, so the write is NotFound.
        var at = text[..^1];

        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => byClass.ReplaceAsync(new Link { Code = "absent", ToText = at, Names = [at] }));
    }
}
