using Moltaro.Lattice.TestSupport.Views;

namespace Moltaro.Lattice.Tests.Views;

public sealed class TypedViewBindingTests
{
    [Fact(Skip = TypedViewSupport.NotImplemented)]
    public async Task The_same_CLR_byte_array_is_viewed_as_binary_or_a_list_using_its_chosen_projection()
    {
        await using var binary = await TestDatabases.OpenMasterAsync(Schema("Binary", true));
        await using var list = await TestDatabases.OpenMasterAsync(Schema("List", true, reorder: true));
        await using var anotherBinary = await TestDatabases.OpenMasterAsync(Schema("Binary", true, reorder: true));
        var binaryItems = binary.Set<ByteProjectionModel>();
        var listItems = list.Set<ByteProjectionModel>();
        var anotherBinaryItems = anotherBinary.Set<ByteProjectionModel>();
        await binaryItems.InsertAsync(new ByteProjectionModel { Id = 1, Data = [11, 42] });
        await listItems.InsertAsync(new ByteProjectionModel { Id = 1, Data = [11, 43] });
        await anotherBinaryItems.InsertAsync(new ByteProjectionModel { Id = 1, Data = [11, 44] });

        Assert.True(binaryItems.TryView(1, static (BinaryProjection view) => view.Data.GetPart(0)[1], out var binaryByte));
        Assert.True(binary.Set("Item").TryView(1, static view => view["Data"].GetPart(0)[1], out var namedBinary));
        Assert.True(listItems.TryView(1, static (ListProjection view) => view.Data[1].Get<byte>(), out var listByte));
        Assert.True(list.Set("Item").TryView(1, static view => view["Data"].List[1].Get<byte>(), out var namedList));
        Assert.True(anotherBinaryItems.TryView(1, static (BinaryProjection view) => view.Data.GetPart(0)[1], out var secondInstanceByte));

        Assert.Equal(new byte[] { 42, 43, 44 }, new[] { binaryByte, listByte, secondInstanceByte });
        Assert.Equal(binaryByte, namedBinary);
        Assert.Equal(listByte, namedList);

        AssertFieldFailure("Data", () => binaryItems.TryView(1, static (ListProjection _) => Unexpected(), out _));
        AssertFieldFailure("Data", () => listItems.TryView(1, static (BinaryProjection _) => Unexpected(), out _));

        Assert.Null(anotherBinaryItems.Get(99));
        AssertFieldFailure("Data", () => anotherBinaryItems.TryView(99, static (ListProjection _) => Unexpected(), out _));
    }

    [Fact(Skip = TypedViewSupport.NotImplemented)]
    public async Task Optional_views_distinguish_a_present_default_scalar_and_empty_list_from_absence()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema("Binary", true));
        var items = database.Set<ByteProjectionModel>();
        var records = database.Set("Item");
        await items.InsertAsync(new ByteProjectionModel { Id = 1, Level = 0, Numbers = [] });
        await items.InsertAsync(new ByteProjectionModel { Id = 2 });
        await items.InsertAsync(new ByteProjectionModel { Id = 3, Level = 7, Numbers = [17] });

        (int? Level, bool HasList, int Count)[] expected = [(0, true, 0), (null, false, 0), (7, true, 1)];
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.True(items.TryView(index + 1, static (BinaryProjection view) =>
            {
                var numbers = view.Numbers;
                return (view.Level, numbers.HasValue, numbers.HasValue ? numbers.Value.Count : 0);
            }, out var byClass));
            Assert.True(records.TryView(index + 1, static view =>
            {
                var level = view["Level"];
                var numbers = view["Numbers"];
                return (level.IsNull ? (int?)null : level.Get<int>(), !numbers.IsNull, numbers.IsNull ? 0 : numbers.List.Count);
            }, out var byName));
            Assert.Equal(expected[index], byName);
            Assert.Equal(byName, byClass);
        }

        Assert.Throws<InvalidOperationException>(() => items.TryView(2, static (BinaryProjection view) => view.Numbers.Value.Count, out _));
    }

    [Theory(Skip = TypedViewSupport.NotImplemented)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_nullable_list_projection_disagreement_is_named_before_the_viewer(bool schemaNullable)
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema("Binary", schemaNullable));
        var items = database.Set<ByteProjectionModel>();
        await items.InsertAsync(new ByteProjectionModel { Id = 1, Numbers = [] });
        Assert.NotNull(items.Get(1));

        if (schemaNullable)
        {
            AssertFieldFailure("Numbers", () => items.TryView(1, static (RequiredListProjection _) => Unexpected(), out _));
        }
        else
        {
            AssertFieldFailure("Numbers", () => items.TryView(1, static (BinaryProjection _) => Unexpected(), out _));
        }

        var session = new LatticeSession();
        var rejected = schemaNullable
            ? items.ViewAsync(1, session, static (RequiredListProjection _) => Unexpected())
            : items.ViewAsync(1, session, static (BinaryProjection _) => Unexpected());
        var error = await Assert.ThrowsAsync<LatticeException>(() => rejected.AsTask());
        Assert.Equal(LatticeErrorEnum.BindingFailed, error.Error);
        Assert.Contains("Numbers", error.Message, StringComparison.Ordinal);
        Assert.Null(session.MinPosition);
    }

    [Theory(Skip = TypedViewSupport.NotImplemented)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_oblivious_model_uses_the_explicit_nullability_of_the_view(bool schemaNullable)
    {
        await using var database = await TestDatabases.OpenMasterAsync(LatticeSchema.Compile($$"""
            { "EntityTypes": [
              { "Id": 1, "Name": "Item", "ClrType": "{{typeof(ObliviousProjectionModel).FullName}}",
                "Fields": [
                  { "Id": 1, "Name": "Id", "Type": "Int32" },
                  { "Id": 2, "Name": "Numbers", "Type": "List", "Element": { "Type": "Int32" }, "Nullable": {{NullableToken(schemaNullable)}} }
                ], "PrimaryKey": [1] }
            ] }
            """));
        var items = database.Set<ObliviousProjectionModel>();
        await items.InsertAsync(new ObliviousProjectionModel { Id = 1, Numbers = [17] });

        if (schemaNullable)
        {
            Assert.True(items.TryView(1, static (ObliviousOptionalListProjection view) => view.Numbers.Value[0].Get<int>(), out var value));
            Assert.Equal(17, value);
            AssertFieldFailure("Numbers", () => items.TryView(1, static (ObliviousRequiredListProjection _) => Unexpected(), out _));
        }
        else
        {
            Assert.True(items.TryView(1, static (ObliviousRequiredListProjection view) => view.Numbers[0].Get<int>(), out var value));
            Assert.Equal(17, value);
            AssertFieldFailure("Numbers", () => items.TryView(1, static (ObliviousOptionalListProjection _) => Unexpected(), out _));
        }
    }

    private static LatticeSchema Schema(string dataKind, bool nullable, bool reorder = false)
    {
        var element = dataKind == "List" ? """, "Element": { "Type": "UInt8" }""" : "";
        var id = """{ "Id": 1, "Name": "Id", "Type": "Int32" }""";
        var data = $$"""{ "Id": 3, "Name": "Data", "Type": "{{dataKind}}"{{element}} }""";
        var numbers = $$"""{ "Id": 4, "Name": "Numbers", "Type": "List", "Element": { "Type": "Int32" }, "Nullable": {{NullableToken(nullable)}} }""";
        var fields = reorder ? $"{numbers}, {data}, {id}" : $"{id}, {data}, {numbers}";
        return LatticeSchema.Compile($$"""
            { "EntityTypes": [
              { "Id": 1, "Name": "Item", "ClrType": "{{typeof(ByteProjectionModel).FullName}}",
                "Fields": [
                  {{fields}},
                  { "Id": 2, "Name": "Level", "Type": "Int32", "Nullable": true }
                ], "PrimaryKey": [1] }
            ] }
            """);
    }

    private static string NullableToken(bool nullable) => nullable ? "true" : "false";

    private static void AssertFieldFailure(string field, Action read)
    {
        var error = Assert.Throws<LatticeException>(read);
        Assert.Equal(LatticeErrorEnum.BindingFailed, error.Error);
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    private static int Unexpected() => throw new InvalidOperationException("A mismatched projection ran its viewer.");
}
