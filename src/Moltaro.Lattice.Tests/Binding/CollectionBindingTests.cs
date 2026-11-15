using System.Collections.ObjectModel;

namespace Moltaro.Lattice.Tests.Binding;

// In a bound class a List is a property of List<T> or T[], a Map of Dictionary<K, V> and a Set
// of HashSet<T>. T, K and V follow the same rule as a property: a scalar is its .NET type, or
// Nullable<> of it where its position is nullable; an Enum is its underlying integer or a .NET
// enum that agrees with the enum type; a reference is the key type of its target; a
// collection inside a collection follows this rule again. Any other type is refused with
// BindingFailed and the property named. Interface types are refused for now.
public sealed class CollectionBindingTests
{
    private enum ColorEnum
    {
        Red = 1,
        Green = 2,
    }

    // Declares Blue, which the enum type Color does not.
    private enum PaletteEnum
    {
        Red = 1,
        Green = 2,
        Blue = 4,
    }

    private const string ListOfInt64 = "{ 'Id': 2, 'Name': 'Value', 'Type': 'List', 'Element': { 'Type': 'Int64' } }";
    private const string ListOfNullableInt32 = "{ 'Id': 2, 'Name': 'Value', 'Type': 'List', 'Element': { 'Type': 'Int32', 'Nullable': true } }";
    private const string MapOfStringToNullableInt32 = "{ 'Id': 2, 'Name': 'Value', 'Type': 'Map', 'Key': { 'Type': 'String' }, 'Value': { 'Type': 'Int32', 'Nullable': true } }";
    private const string SetOfGuid = "{ 'Id': 2, 'Name': 'Value', 'Type': 'Set', 'Element': { 'Type': 'Guid' } }";
    private const string ListOfColor = "{ 'Id': 2, 'Name': 'Value', 'Type': 'List', 'Element': { 'Type': 'Enum', 'Enum': 'Color' } }";

    [Fact]
    public async Task List_binds_as_a_List_or_an_array_of_its_element_type()
    {
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<List<long>>>(ListOfInt64);
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<long[]>>(ListOfInt64);
    }

    [Fact]
    public async Task List_as_any_other_type_is_refused_with_the_property_named()
    {
        // Interfaces and other collections.
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<IList<long>>>(ListOfInt64, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<IEnumerable<long>>>(ListOfInt64, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<IReadOnlyList<long>>>(ListOfInt64, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<ICollection<long>>>(ListOfInt64, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<Collection<long>>>(ListOfInt64, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<LinkedList<long>>>(ListOfInt64, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<HashSet<long>>>(ListOfInt64, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<object>>(ListOfInt64, "Value");

        // Another element type.
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<List<int>>>(ListOfInt64, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<List<object>>>(ListOfInt64, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<int[]>>(ListOfInt64, "Value");
    }

    [Fact]
    public async Task Element_of_a_value_type_is_Nullable_exactly_where_it_is_nullable()
    {
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<List<int?>>>(ListOfNullableInt32);
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<int?[]>>(ListOfNullableInt32);

        // An element that may be null needs a type that holds null, and one that may not
        // needs a type that does not.
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<List<int>>>(ListOfNullableInt32, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<List<long?>>>(ListOfInt64, "Value");
    }

    [Fact]
    public async Task Map_binds_as_a_Dictionary_of_its_key_and_value_types_and_as_nothing_else()
    {
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<Dictionary<string, int?>>>(MapOfStringToNullableInt32);

        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<IDictionary<string, int?>>>(MapOfStringToNullableInt32, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<IReadOnlyDictionary<string, int?>>>(MapOfStringToNullableInt32, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<SortedDictionary<string, int?>>>(MapOfStringToNullableInt32, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<List<KeyValuePair<string, int?>>>>(MapOfStringToNullableInt32, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<KeyValuePair<string, int?>[]>>(MapOfStringToNullableInt32, "Value");

        // Another key type, another value type.
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<Dictionary<object, int?>>>(MapOfStringToNullableInt32, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<Dictionary<string, int>>>(MapOfStringToNullableInt32, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<Dictionary<string, long?>>>(MapOfStringToNullableInt32, "Value");
    }

    [Fact]
    public async Task Set_binds_as_a_HashSet_of_its_element_type_and_as_nothing_else()
    {
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<HashSet<Guid>>>(SetOfGuid);

        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<ISet<Guid>>>(SetOfGuid, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<IReadOnlySet<Guid>>>(SetOfGuid, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<SortedSet<Guid>>>(SetOfGuid, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<List<Guid>>>(SetOfGuid, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<Guid[]>>(SetOfGuid, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<HashSet<string>>>(SetOfGuid, "Value");
    }

    [Fact]
    public async Task Collections_inside_collections_bind_by_the_same_rule()
    {
        const string Fields = """
            { 'Id': 2, 'Name': 'Layers', 'Type': 'List', 'Element': { 'Type': 'List', 'Element': { 'Type': 'Int64' } } },
            { 'Id': 3, 'Name': 'Grid', 'Type': 'List', 'Element': { 'Type': 'List', 'Element': { 'Type': 'Int64' } } },
            { 'Id': 4, 'Name': 'Members', 'Type': 'Map', 'Key': { 'Type': 'String' }, 'Value': { 'Type': 'List', 'Element': { 'Type': 'Reference', 'EntityType': 'Group' } } },
            { 'Id': 5, 'Name': 'Calendar', 'Type': 'Map', 'Key': { 'Type': 'Guid' }, 'Value': { 'Type': 'Map', 'Key': { 'Type': 'Int32' }, 'Value': { 'Type': 'Set', 'Element': { 'Type': 'DateOnly' } } } },
            { 'Id': 6, 'Name': 'Blobs', 'Type': 'List', 'Element': { 'Type': 'Binary', 'Nullable': true } }
            """;

        await BindingSchema.ExpectBoundAsync<Nested>(Fields);
        await BindingSchema.ExpectBoundAsync<NestedAsArrays>(Fields);

        // The inner element is checked as well.
        await BindingSchema.ExpectNotBoundAsync<NestedWithWrongInnerType>(Fields, "Layers");
    }

    [Fact]
    public async Task Reference_element_binds_as_the_key_type_of_its_target()
    {
        // Group has a Guid key, Holder an Int64 key.
        const string Fields = """
            { 'Id': 2, 'Name': 'Groups', 'Type': 'List', 'Element': { 'Type': 'Reference', 'EntityType': 'Group' } },
            { 'Id': 3, 'Name': 'Peers', 'Type': 'Set', 'Element': { 'Type': 'Reference', 'EntityType': 'Holder' } },
            { 'Id': 4, 'Name': 'Votes', 'Type': 'Map', 'Key': { 'Type': 'Reference', 'EntityType': 'Group' }, 'Value': { 'Type': 'Int32' } }
            """;

        await BindingSchema.ExpectBoundAsync<WithReferences>(Fields);
        await BindingSchema.ExpectNotBoundAsync<WithReferencesAsInt32>(Fields, "Peers");
    }

    [Fact]
    public async Task Enum_element_binds_as_a_dotnet_enum_that_agrees_or_as_its_underlying_integer()
    {
        const string Fields = """
            { 'Id': 2, 'Name': 'Paints', 'Type': 'List', 'Element': { 'Type': 'Enum', 'Enum': 'Color' } },
            { 'Id': 3, 'Name': 'Choices', 'Type': 'List', 'Element': { 'Type': 'Enum', 'Enum': 'Color', 'Nullable': true } },
            { 'Id': 4, 'Name': 'Distinct', 'Type': 'Set', 'Element': { 'Type': 'Enum', 'Enum': 'Color' } },
            { 'Id': 5, 'Name': 'Counts', 'Type': 'Map', 'Key': { 'Type': 'Enum', 'Enum': 'Color' }, 'Value': { 'Type': 'Int32' } }
            """;

        await BindingSchema.ExpectBoundAsync<WithEnums>(Fields);
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<List<int>>>(ListOfColor);
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<ColorEnum[]>>(ListOfColor);
    }

    [Fact]
    public async Task Dotnet_enum_element_that_does_not_agree_is_refused_with_the_property_named()
    {
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<List<PaletteEnum>>>(ListOfColor, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<List<ColorEnum?>>>(ListOfColor, "Value");
    }

    private sealed class Nested
    {
        public long Id { get; set; }

        public List<List<long>> Layers { get; set; } = [];

        public List<long[]> Grid { get; set; } = [];

        public Dictionary<string, List<Guid>> Members { get; set; } = [];

        public Dictionary<Guid, Dictionary<int, HashSet<DateOnly>>> Calendar { get; set; } = [];

        public List<byte[]?> Blobs { get; set; } = [];
    }

    private sealed class NestedAsArrays
    {
        public long Id { get; set; }

        public long[][] Layers { get; set; } = [];

        public List<long>[] Grid { get; set; } = [];

        public Dictionary<string, Guid[]> Members { get; set; } = [];

        public Dictionary<Guid, Dictionary<int, HashSet<DateOnly>>> Calendar { get; set; } = [];

        public byte[]?[] Blobs { get; set; } = [];
    }

    private sealed class NestedWithWrongInnerType
    {
        public long Id { get; set; }

        public List<List<int>> Layers { get; set; } = [];

        public List<long[]> Grid { get; set; } = [];

        public Dictionary<string, List<Guid>> Members { get; set; } = [];

        public Dictionary<Guid, Dictionary<int, HashSet<DateOnly>>> Calendar { get; set; } = [];

        public List<byte[]?> Blobs { get; set; } = [];
    }

    private sealed class WithReferences
    {
        public long Id { get; set; }

        public List<Guid> Groups { get; set; } = [];

        public HashSet<long> Peers { get; set; } = [];

        public Dictionary<Guid, int> Votes { get; set; } = [];
    }

    private sealed class WithReferencesAsInt32
    {
        public long Id { get; set; }

        public List<Guid> Groups { get; set; } = [];

        public HashSet<int> Peers { get; set; } = [];

        public Dictionary<Guid, int> Votes { get; set; } = [];
    }

    private sealed class WithEnums
    {
        public long Id { get; set; }

        public List<ColorEnum> Paints { get; set; } = [];

        public List<ColorEnum?> Choices { get; set; } = [];

        public HashSet<ColorEnum> Distinct { get; set; } = [];

        public Dictionary<ColorEnum, int> Counts { get; set; } = [];
    }
}
