namespace Moltaro.Lattice.Tests;

// Enum types: named values of one of the eight integer types, Int32 unless the schema names
// another. An ordinary enum type accepts only its declared values; a value may have several
// names, one of them canonical. A field of an enum type holds its values.
public sealed class EnumTypeTests
{
    private const string Json = """
        {
          "Enums": [
            {
              "Id": 1, "Name": "JobState", "UnderlyingType": "UInt8",
              "Members": [
                { "Name": "New", "Value": 10 },
                { "Name": "Running", "Value": 30 },
                { "Name": "Done", "Value": 20 }
              ]
            },
            {
              "Id": 2, "Name": "State",
              "Members": [
                { "Name": "Disabled", "Value": 0 },
                { "Name": "Active", "Value": 1, "Canonical": true },
                { "Name": "Enabled", "Value": 1 }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Job",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Status", "Type": "Enum", "Enum": "JobState" },
                { "Id": 3, "Name": "Previous", "Type": "Enum", "Enum": "JobState", "Nullable": true },
                { "Id": 4, "Name": "Owner", "Type": "Enum", "Enum": "State" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByStatus", "Fields": [2] } ]
            }
          ]
        }
        """;

    [Fact]
    public void Enum_type_declares_its_underlying_type_and_its_members_in_declaration_order()
    {
        var schema = LatticeSchema.Compile(Json);

        Assert.Equal(["JobState", "State"], schema.EnumTypes.Select(enumType => enumType.Name));
        Assert.True(schema.TryGetEnumType("JobState", out var jobState));
        Assert.False(schema.TryGetEnumType("Job", out _));

        Assert.Equal(1, jobState.Id);
        Assert.Equal(LatticeFieldTypeEnum.UInt8, jobState.UnderlyingType);
        Assert.False(jobState.IsFlags);

        // The members keep the order of the descriptor; the values decide nothing about it.
        Assert.Equal(
            [("New", (Int128)10), ("Running", (Int128)30), ("Done", (Int128)20)],
            jobState.Members.Select(member => (member.Name, member.Value)));
    }

    [Fact]
    public void Enum_type_is_Int32_and_not_flags_unless_the_descriptor_says_otherwise()
    {
        var schema = LatticeSchema.Compile(Json);
        Assert.True(schema.TryGetEnumType("State", out var state));

        Assert.Equal(LatticeFieldTypeEnum.Int32, state.UnderlyingType);
        Assert.False(state.IsFlags);
    }

    [Fact]
    public void Every_value_has_exactly_one_canonical_name()
    {
        var schema = LatticeSchema.Compile(Json);
        Assert.True(schema.TryGetEnumType("State", out var state));

        // The only name of a value is canonical without saying so; of several names, the one
        // the descriptor marks.
        Assert.Equal(
            [("Disabled", true), ("Active", true), ("Enabled", false)],
            state.Members.Select(member => (member.Name, member.IsCanonical)));

        // The only name of a value may also be marked.
        var marked = LatticeSchema.Compile(Descriptor.With(Json, "{ 'Name': 'New', 'Value': 10 },", "{ 'Name': 'New', 'Value': 10, 'Canonical': true },"));
        Assert.True(marked.TryGetEnumType("JobState", out var jobState));
        Assert.True(jobState.Members[0].IsCanonical);
    }

    [Theory]
    // No canonical name among several.
    [InlineData("{ 'Name': 'Active', 'Value': 1, 'Canonical': true },", "{ 'Name': 'Active', 'Value': 1 },")]
    // Two canonical names for one value.
    [InlineData("{ 'Name': 'Enabled', 'Value': 1 }", "{ 'Name': 'Enabled', 'Value': 1, 'Canonical': true }")]
    public void Value_with_several_names_and_not_exactly_one_canonical_name_is_refused(string part, string replacement)
    {
        Descriptor.ExpectRefused(Json, part, replacement);
    }

    [Fact]
    public void Field_of_an_enum_type_holds_its_values_as_its_underlying_integer()
    {
        var schema = LatticeSchema.Compile(Json);
        Assert.True(schema.TryGetEnumType("JobState", out var jobState));
        Assert.True(schema.TryGetEnumType("State", out var state));
        Assert.True(schema.TryGetEntityType("Job", out var job));

        Assert.True(job.TryGetField("Status", out var status));
        Assert.Equal(LatticeFieldTypeEnum.Enum, status.Declaration.Type);
        Assert.Same(jobState, status.Declaration.EnumType);
        Assert.Equal(typeof(byte), status.Declaration.ValueType);
        Assert.False(status.Declaration.IsNullable);
        Assert.Null(status.Declaration.Comparison);
        Assert.Null(status.Declaration.StringStorage);

        Assert.True(job.TryGetField("Previous", out var previous));
        Assert.Same(jobState, previous.Declaration.EnumType);
        Assert.True(previous.Declaration.IsNullable);

        Assert.True(job.TryGetField("Owner", out var owner));
        Assert.Same(state, owner.Declaration.EnumType);
        Assert.Equal(typeof(int), owner.Declaration.ValueType);

        // An index may cover a field of an enum type.
        Assert.True(job.TryGetIndex("ByStatus", out var byStatus));
        Assert.Same(status, Assert.Single(byStatus.Parts).Field);
    }

    [Theory]
    [InlineData("Int8", typeof(sbyte))]
    [InlineData("UInt8", typeof(byte))]
    [InlineData("Int16", typeof(short))]
    [InlineData("UInt16", typeof(ushort))]
    [InlineData("Int32", typeof(int))]
    [InlineData("UInt32", typeof(uint))]
    [InlineData("Int64", typeof(long))]
    [InlineData("UInt64", typeof(ulong))]
    public void Underlying_type_is_one_of_the_eight_integer_types(string underlyingType, Type valueType)
    {
        var json = Descriptor.With(Json, "'UnderlyingType': 'UInt8'", $"'UnderlyingType': '{underlyingType}'");

        var schema = LatticeSchema.Compile(json);

        Assert.True(schema.TryGetEnumType("JobState", out var jobState));
        Assert.Equal(Enum.Parse<LatticeFieldTypeEnum>(underlyingType), jobState.UnderlyingType);
        Assert.True(schema.TryGetEntityType("Job", out var job));
        Assert.True(job.TryGetField("Status", out var status));
        Assert.Equal(valueType, status.Declaration.ValueType);
    }

    [Theory]
    [InlineData("Boolean")]
    [InlineData("Float64")]
    [InlineData("Decimal")]
    [InlineData("String")]
    [InlineData("Guid")]
    [InlineData("Enum")]
    [InlineData("Int128")]
    [InlineData("Byte")]
    public void Underlying_type_that_is_not_one_of_the_eight_integer_types_is_refused(string underlyingType)
    {
        Descriptor.ExpectRefused(Json, "'UnderlyingType': 'UInt8'", $"'UnderlyingType': '{underlyingType}'");
    }

    [Theory]
    [InlineData("Int8", "-128", "127")]
    [InlineData("UInt8", "0", "255")]
    [InlineData("Int16", "-32768", "32767")]
    [InlineData("UInt16", "0", "65535")]
    [InlineData("Int32", "-2147483648", "2147483647")]
    [InlineData("UInt32", "0", "4294967295")]
    [InlineData("Int64", "-9223372036854775808", "9223372036854775807")]
    [InlineData("UInt64", "0", "18446744073709551615")]
    public void Every_value_of_the_underlying_type_can_be_declared_and_is_held_exactly(string underlyingType, string minimum, string maximum)
    {
        var code = EnumSchema.Ordinary(underlyingType, $"{{ 'Name': 'Lowest', 'Value': {minimum} }}, {{ 'Name': 'Highest', 'Value': {maximum} }}");

        Assert.Equal([EnumSchema.Value(minimum), EnumSchema.Value(maximum)], code.Members.Select(member => member.Value));
        Assert.True(code.IsValid(EnumSchema.Value(minimum)));
        Assert.True(code.IsValid(EnumSchema.Value(maximum)));
    }

    [Theory]
    [InlineData("Int8", "128")]
    [InlineData("Int8", "-129")]
    [InlineData("UInt8", "256")]
    [InlineData("UInt8", "-1")]
    [InlineData("Int16", "32768")]
    [InlineData("Int16", "-32769")]
    [InlineData("UInt16", "65536")]
    [InlineData("UInt16", "-1")]
    [InlineData("Int32", "2147483648")]
    [InlineData("Int32", "-2147483649")]
    [InlineData("UInt32", "4294967296")]
    [InlineData("UInt32", "-1")]
    [InlineData("Int64", "9223372036854775808")]
    [InlineData("Int64", "-9223372036854775809")]
    [InlineData("UInt64", "18446744073709551616")]
    [InlineData("UInt64", "-1")]
    // Not an integer.
    [InlineData("Int32", "1.5")]
    public void Member_value_outside_the_range_of_the_underlying_type_is_refused(string underlyingType, string value)
    {
        // The same enum type with a value inside the range compiles.
        EnumSchema.Ordinary(underlyingType, "{ 'Name': 'Inside', 'Value': 1 }");

        var json = EnumSchema.Json(underlyingType, isFlags: false, $"{{ 'Name': 'Outside', 'Value': {value} }}");

        TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => LatticeSchema.Compile(json));
    }

    [Theory]
    // Two members with one name.
    [InlineData("{ 'Name': 'Done', 'Value': 20 }", "{ 'Name': 'New', 'Value': 20 }")]
    // A member without a name, or with an empty one.
    [InlineData("{ 'Name': 'Done', 'Value': 20 }", "{ 'Value': 20 }")]
    [InlineData("{ 'Name': 'Done', 'Value': 20 }", "{ 'Name': '', 'Value': 20 }")]
    // A member without a value: values are explicit and never follow from the order.
    [InlineData("{ 'Name': 'Done', 'Value': 20 }", "{ 'Name': 'Done' }")]
    // Two enum types with one name.
    [InlineData("'Enums': [", "'Enums': [ { 'Id': 3, 'Name': 'State', 'Members': [ { 'Name': 'Off', 'Value': 0 } ] },")]
    public void Names_and_values_of_an_enum_type_must_be_declared_and_unambiguous(string part, string replacement)
    {
        Descriptor.ExpectRefused(Json, part, replacement);
    }

    [Theory]
    // A field of type Enum that names no enum type.
    [InlineData("{ 'Id': 4, 'Name': 'Owner', 'Type': 'Enum', 'Enum': 'State' }", "{ 'Id': 4, 'Name': 'Owner', 'Type': 'Enum' }")]
    // An enum type the schema does not declare.
    [InlineData("{ 'Id': 4, 'Name': 'Owner', 'Type': 'Enum', 'Enum': 'State' }", "{ 'Id': 4, 'Name': 'Owner', 'Type': 'Enum', 'Enum': 'Status' }")]
    // An entity type is not an enum type.
    [InlineData("{ 'Id': 4, 'Name': 'Owner', 'Type': 'Enum', 'Enum': 'State' }", "{ 'Id': 4, 'Name': 'Owner', 'Type': 'Enum', 'Enum': 'Job' }")]
    // An enum type for a field that is not of type Enum.
    [InlineData("{ 'Id': 1, 'Name': 'Id', 'Type': 'Int64' }", "{ 'Id': 1, 'Name': 'Id', 'Type': 'Int64', 'Enum': 'State' }")]
    public void Field_of_type_Enum_names_a_declared_enum_type_and_no_other_field_names_one(string part, string replacement)
    {
        Descriptor.ExpectRefused(Json, part, replacement);
    }

    [Fact]
    public void Ordinary_enum_type_accepts_only_its_declared_values()
    {
        var schema = LatticeSchema.Compile(Json);
        Assert.True(schema.TryGetEnumType("JobState", out var jobState));
        Assert.True(schema.TryGetEnumType("State", out var state));

        Assert.True(jobState.IsValid(10));
        Assert.True(jobState.IsValid(20));
        Assert.True(jobState.IsValid(30));

        // Zero is a value like any other: valid only when it is declared.
        Assert.False(jobState.IsValid(0));
        Assert.False(jobState.IsValid(11));
        Assert.False(jobState.IsValid(40));
        Assert.False(jobState.IsValid(-10));

        // A value with two names is one value.
        Assert.True(state.IsValid(0));
        Assert.True(state.IsValid(1));
        Assert.False(state.IsValid(2));
    }

    [Theory]
    // The declared value, and a value outside the range of the underlying type with the same
    // bits in its width.
    [InlineData("Int8", "-1", "255")]
    [InlineData("UInt8", "255", "-1")]
    [InlineData("UInt8", "255", "511")]
    [InlineData("Int16", "-1", "65535")]
    [InlineData("UInt16", "65535", "-1")]
    [InlineData("Int32", "-1", "4294967295")]
    [InlineData("UInt32", "4294967295", "-1")]
    [InlineData("Int64", "-1", "18446744073709551615")]
    [InlineData("UInt64", "18446744073709551615", "-1")]
    public void Value_outside_the_range_of_the_underlying_type_is_never_valid(string underlyingType, string declared, string outside)
    {
        var code = EnumSchema.Ordinary(underlyingType, $"{{ 'Name': 'Declared', 'Value': {declared} }}");

        Assert.True(code.IsValid(EnumSchema.Value(declared)));
        Assert.False(code.IsValid(EnumSchema.Value(outside)));
    }
}
