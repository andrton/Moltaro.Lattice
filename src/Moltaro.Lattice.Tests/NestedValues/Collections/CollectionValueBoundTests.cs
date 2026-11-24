using Moltaro.Lattice.TestSupport.Values;

namespace Moltaro.Lattice.Tests.NestedValues.Collections;

// The encoded String and Binary bound applies to each Map key, Map value and Set member
// (LatticeFieldTypeEnum.String and Binary). Test the validation step before write conditions.
[Collection<LargeValueCollection>]
public sealed class CollectionValueBoundTests
{
    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "BoundItem", "ClrType": "{{typeof(CollectionBoundItem).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Map", "Type": "Map", "Key": { "Type": "String" }, "Value": { "Type": "Binary" } },
                { "Id": 3, "Name": "Set", "Type": "Set", "Element": { "Type": "String" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Fact]
    public async Task An_over_bound_Map_key_Map_value_or_Set_member_is_refused_before_the_write_condition()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var typed = database.Set<CollectionBoundItem>();
        var named = database.Set("BoundItem");
        var text = ValueBound.TextOneByteAboveInUtf8;

        await TestData.ExpectValidationFailedAsync("Map", () => typed.ReplaceAsync(new CollectionBoundItem { Id = 1, Map = new() { [text] = [] } }));
        await TestData.ExpectValidationFailedAsync("Map", () => typed.ReplaceAsync(new CollectionBoundItem { Id = 1, Map = new() { ["key"] = GC.AllocateUninitializedArray<byte>(ValueBound.Bytes + 1) } }));
        await TestData.ExpectValidationFailedAsync("Set", () => typed.ReplaceAsync(new CollectionBoundItem { Id = 1, Set = [text] }));

        var item = new LatticeRecord(named.EntityType) { ["Id"] = 1L, ["Map"] = new Dictionary<string, byte[]> { [text] = [] }, ["Set"] = Array.Empty<string>() };
        await TestData.ExpectValidationFailedAsync("Map", () => named.ReplaceAsync(item));
        item.Get<Dictionary<string, byte[]>>("Map")!.Clear();
        item.Get<Dictionary<string, byte[]>>("Map")!.Add("key", GC.AllocateUninitializedArray<byte>(ValueBound.Bytes + 1));
        await TestData.ExpectValidationFailedAsync("Map", () => named.ReplaceAsync(item));
        item.Get<Dictionary<string, byte[]>>("Map")!.Clear();
        item.Get<HashSet<string>>("Set")!.Add(text);
        await TestData.ExpectValidationFailedAsync("Set", () => named.ReplaceAsync(item));

        var batch = database.CreateWriteBatch();
        TestData.ExpectValidationFailed("Map", () => batch.Insert(new CollectionBoundItem { Id = 1, Map = new() { [text] = [] } }));
        Assert.Equal(0, batch.Count);
        Assert.Null(named.Get(1L));
    }

    [Fact]
    public async Task A_Map_key_Map_value_and_Set_member_of_exactly_the_bound_pass_validation()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var items = database.Set<CollectionBoundItem>();
        var text = ValueBound.TextOneByteAboveInUtf8[..^1];

        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => items.ReplaceAsync(new CollectionBoundItem { Id = 1, Map = new() { [text] = [] } }));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => items.ReplaceAsync(new CollectionBoundItem { Id = 1, Map = new() { ["key"] = new byte[ValueBound.Bytes] } }));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => items.ReplaceAsync(new CollectionBoundItem { Id = 1, Set = [text] }));
    }
}
