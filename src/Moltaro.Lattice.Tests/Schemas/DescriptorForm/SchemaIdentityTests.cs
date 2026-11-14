namespace Moltaro.Lattice.Tests.Schemas.DescriptorForm;

// Stable identities: every enum type, entity type, field and index has a positive identity,
// unique within its scope. Enum types and entity types each have their own scope in the
// schema; fields and indexes each have their own scope in their entity type.
public sealed class SchemaIdentityTests
{
    private const string Json = """
        {
          "Enums": [
            { "Id": 1, "Name": "Color", "Members": [ { "Name": "Red", "Value": 0 } ] },
            { "Id": 2, "Name": "Size", "Members": [ { "Name": "Small", "Value": 0 } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Car",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Model", "Type": "String" },
                { "Id": 3, "Name": "Paint", "Type": "Enum", "Enum": "Color" },
                { "Id": 4, "Name": "Seats", "Type": "Int32" }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByModel", "Fields": [2] },
                { "Id": 2, "Name": "ByPaint", "Fields": [3] }
              ]
            },
            {
              "Id": 2, "Name": "Bus",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2147483647, "Name": "Line", "Type": "String" }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByLine", "Fields": [2147483647] }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Identities_are_unique_within_their_scope_only()
    {
        var schema = LatticeSchema.Compile(Json);

        // An enum type and an entity type may have the same identity.
        Assert.Equal([1, 2], schema.EnumTypes.Select(enumType => enumType.Id));
        Assert.Equal([1, 2], schema.EntityTypes.Select(entityType => entityType.Id));

        // Two entity types may each have a field and an index with the identity 1, and an
        // index may have the identity of a field.
        Assert.True(schema.TryGetEntityType("Car", out var car));
        Assert.True(schema.TryGetEntityType("Bus", out var bus));
        Assert.Equal([1, 2, 3, 4], car.Fields.Select(field => field.Id));
        Assert.Equal([1, 2], car.Indexes.Select(index => index.Id));
        Assert.Equal([1], bus.Indexes.Select(index => index.Id));

        // Every identity an int can hold from 1 up is allowed.
        Assert.True(bus.TryGetField("Line", out var line));
        Assert.Equal(int.MaxValue, line.Id);
    }

    [Theory]
    // An entity type: zero, negative, missing, the identity of another entity type.
    [InlineData("'Id': 1, 'Name': 'Car',", "'Id': 0, 'Name': 'Car',", "Car")]
    [InlineData("'Id': 1, 'Name': 'Car',", "'Id': -1, 'Name': 'Car',", "Car")]
    [InlineData("'Id': 1, 'Name': 'Car',", "'Name': 'Car',", "Car")]
    [InlineData("'Id': 2, 'Name': 'Bus',", "'Id': 1, 'Name': 'Bus',", "Bus")]
    // An enum type.
    [InlineData("{ 'Id': 1, 'Name': 'Color',", "{ 'Id': 0, 'Name': 'Color',", "Color")]
    [InlineData("{ 'Id': 1, 'Name': 'Color',", "{ 'Id': -1, 'Name': 'Color',", "Color")]
    [InlineData("{ 'Id': 1, 'Name': 'Color',", "{ 'Name': 'Color',", "Color")]
    [InlineData("{ 'Id': 2, 'Name': 'Size',", "{ 'Id': 1, 'Name': 'Size',", "Size")]
    // A field.
    [InlineData("{ 'Id': 4, 'Name': 'Seats',", "{ 'Id': 0, 'Name': 'Seats',", "Car", "Seats")]
    [InlineData("{ 'Id': 4, 'Name': 'Seats',", "{ 'Id': -1, 'Name': 'Seats',", "Car", "Seats")]
    [InlineData("{ 'Id': 4, 'Name': 'Seats',", "{ 'Name': 'Seats',", "Car", "Seats")]
    [InlineData("{ 'Id': 4, 'Name': 'Seats',", "{ 'Id': 2, 'Name': 'Seats',", "Car", "Seats")]
    // An index.
    [InlineData("{ 'Id': 2, 'Name': 'ByPaint',", "{ 'Id': 0, 'Name': 'ByPaint',", "Car", "ByPaint")]
    [InlineData("{ 'Id': 2, 'Name': 'ByPaint',", "{ 'Id': -1, 'Name': 'ByPaint',", "Car", "ByPaint")]
    [InlineData("{ 'Id': 2, 'Name': 'ByPaint',", "{ 'Name': 'ByPaint',", "Car", "ByPaint")]
    [InlineData("{ 'Id': 2, 'Name': 'ByPaint',", "{ 'Id': 1, 'Name': 'ByPaint',", "Car", "ByPaint")]
    public void Identity_that_is_zero_negative_missing_or_repeated_is_refused_with_its_place(string part, string replacement, params string[] place)
    {
        var exception = Descriptor.ExpectRefused(Json, part, replacement);

        // The message names where the identity is: the enum type or the entity type, and
        // within an entity type the field or the index. Of two declarations with the same
        // identity, it names the one that repeats it.
        Assert.All(place, name => Assert.Contains(name, exception.Message));
    }

    [Theory]
    // Larger than an int holds.
    [InlineData("{ 'Id': 4, 'Name': 'Seats',", "{ 'Id': 2147483648, 'Name': 'Seats',")]
    [InlineData("{ 'Id': 2, 'Name': 'ByPaint',", "{ 'Id': 4294967296, 'Name': 'ByPaint',")]
    // Not an integer.
    [InlineData("'Id': 1, 'Name': 'Car',", "'Id': 1.5, 'Name': 'Car',")]
    public void Identity_that_an_int_cannot_hold_is_refused(string part, string replacement)
    {
        Descriptor.ExpectRefused(Json, part, replacement);
    }
}
