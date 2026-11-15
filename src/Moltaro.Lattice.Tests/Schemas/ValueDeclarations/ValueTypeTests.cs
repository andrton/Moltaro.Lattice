namespace Moltaro.Lattice.Tests.Schemas.ValueDeclarations;

// ValueType is the .NET type of a non-null value at a position: what a record holds there. An
// owned value is a LatticeOwnedValue at every depth, whether or not its owned type names a
// class; a reference is the key of its target; a List is a List<T>, a Map a Dictionary<K, V>
// and a Set a HashSet<T>, where T, K and V are the ValueType of the inner declaration, or
// Nullable<> of it when that position is nullable and its type is a value type.
public sealed class ValueTypeTests
{
    [Theory]
    [InlineData("Home", typeof(LatticeOwnedValue))]
    [InlineData("Work", typeof(LatticeOwnedValue))]
    [InlineData("Spot", typeof(LatticeOwnedValue))]
    [InlineData("Manager", typeof(long))]
    [InlineData("Groups", typeof(List<Guid>))]
    [InlineData("Options", typeof(Dictionary<string, string>))]
    [InlineData("Rights", typeof(HashSet<string>))]
    [InlineData("Scores", typeof(List<int?>))]
    [InlineData("Places", typeof(List<LatticeOwnedValue>))]
    [InlineData("Sites", typeof(Dictionary<int, LatticeOwnedValue>))]
    [InlineData("Layers", typeof(List<List<long>>))]
    [InlineData("Members", typeof(Dictionary<string, List<Guid>>))]
    [InlineData("Aliases", typeof(HashSet<string>))]
    [InlineData("Paints", typeof(List<byte>))]
    [InlineData("PaintCounts", typeof(Dictionary<byte, int>))]
    [InlineData("Teams", typeof(HashSet<Guid>))]
    [InlineData("Blobs", typeof(List<byte[]>))]
    [InlineData("Grid", typeof(Dictionary<Guid, Dictionary<int, HashSet<DateOnly>>>))]
    [InlineData("Login", typeof(string))]
    public void Value_type_of_a_field_is_what_a_record_holds_for_it(string fieldName, Type valueType)
    {
        var field = ValueSchema.Field(ValueSchema.Compile(), fieldName);

        Assert.Equal(valueType, field.Declaration.ValueType);
    }

    [Fact]
    public void Value_type_of_an_inner_position_is_the_type_of_a_value_that_is_not_null()
    {
        var schema = ValueSchema.Compile();

        // The elements of Scores may be null; an element that is there is an int.
        Assert.Equal(typeof(int), ValueSchema.Field(schema, "Scores").Declaration.Element!.ValueType);
        Assert.Equal(typeof(string), ValueSchema.Field(schema, "Options").Declaration.Value!.ValueType);
        Assert.Equal(typeof(List<Guid>), ValueSchema.Field(schema, "Members").Declaration.Value!.ValueType);
        Assert.Equal(typeof(LatticeOwnedValue), ValueSchema.Field(schema, "Places").Declaration.Element!.ValueType);
        Assert.Equal(typeof(byte), ValueSchema.Field(schema, "PaintCounts").Declaration.Key!.ValueType);
        Assert.Equal(typeof(Guid), ValueSchema.Field(schema, "Teams").Declaration.Element!.ValueType);
    }

    [Theory]
    [InlineData("City", typeof(string))]
    [InlineData("Floor", typeof(short))]
    [InlineData("Location", typeof(LatticeOwnedValue))]
    [InlineData("Lines", typeof(List<string>))]
    [InlineData("Owner", typeof(Guid))]
    public void Value_type_of_a_field_of_an_owned_type_follows_the_same_rule(string fieldName, Type valueType)
    {
        Assert.True(ValueSchema.OwnedType(ValueSchema.Compile(), "Address").TryGetField(fieldName, out var field));

        Assert.Equal(valueType, field.Declaration.ValueType);
    }
}
