namespace Moltaro.Lattice.Tests;

// An enum type declares at least one member, a Flags type like an ordinary one: an enum type
// without members has no value a field of it could hold. A Flags type whose only member is
// zero holds the empty set and is valid.
public sealed class EmptyEnumTypeTests
{
    [Theory(Skip = NotImplemented.SchemaValues)]
    [InlineData(false)]
    [InlineData(true)]
    public void Enum_type_without_members_is_refused_with_the_enum_type_named(bool isFlags)
    {
        // The same enum type with one member compiles.
        LatticeSchema.Compile(EnumSchema.Json("Int32", isFlags, "{ 'Name': 'None', 'Value': 0 }"));

        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile(EnumSchema.Json("Int32", isFlags, "")));

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains("Code", exception.Message);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Enum_type_without_members_is_refused_also_when_no_field_names_it()
    {
        var json = """
            {
              "Enums": [ { "Id": 1, "Name": "Unused", "Members": [] } ],
              "EntityTypes": [ { "Id": 1, "Name": "Note", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ], "PrimaryKey": [1] } ]
            }
            """;

        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile(json));

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains("Unused", exception.Message);
    }

    [Fact]
    public void Flags_type_whose_only_member_is_zero_is_valid()
    {
        var code = EnumSchema.Flags("UInt8", "{ 'Name': 'None', 'Value': 0 }");

        Assert.True(code.IsValid(0));
        Assert.False(code.IsValid(1));
    }
}
