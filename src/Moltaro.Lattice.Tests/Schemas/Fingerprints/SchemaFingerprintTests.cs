using System.Text.Json.Nodes;

namespace Moltaro.Lattice.Tests.Schemas.Fingerprints;

// The fingerprint of a schema covers what decides how its data is laid out, compared and kept
// unique: the string encoding; every enum type with its identity, underlying type, Flags mark
// and values; every entity type with its identity and primary key, and every field with its
// identity, type, nullability, comparison, string storage and enum type; every index with its
// identity, its fields in order and its Unique mark. Names, classes, the catalog version and
// the order of the descriptor are not part of it.
public sealed class SchemaFingerprintTests
{
    // The fingerprint of FingerprintDescriptor.Json, recorded from the implementation of
    // Epic 1, sprint 2. The same test runs on Linux and on Windows in CI, so passing on both
    // shows that the fingerprint does not depend on the platform. It is recorded again only
    // when a later sprint extends what the fingerprint encodes, such as owned types and
    // collections; for any other change of the code a different value is a defect.
    private const string RecordedFingerprint = "e4d0cb436f115b6f6ca79cbc3f49fcaa69263671068d81435c530f7b3958f97c";

    [Fact]
    public void Same_declarations_give_the_same_fingerprint()
    {
        var first = FingerprintDescriptor.Compile().Fingerprint;
        var second = FingerprintDescriptor.Compile().Fingerprint;
        var other = FingerprintDescriptor.CompileWith("'StringEncoding': 'Utf8',", "'StringEncoding': 'Utf16',").Fingerprint;

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.False(first != second);
        Assert.True(first.Equals((object)second));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(first.ToString(), second.ToString());

        Assert.NotEqual(first, other);
        Assert.True(first != other);
        Assert.False(first.Equals((object)other));

        // 64 lowercase hexadecimal digits: the 32 bytes of a SHA-256 digest.
        Assert.Matches("^[0-9a-f]{64}$", first.ToString());
    }

    [Theory]
    // The string encoding.
    [InlineData("'StringEncoding': 'Utf8',", "'StringEncoding': 'Utf16',")]
    // An enum type: its identity, its underlying type, its Flags mark, its values.
    [InlineData("'Id': 1, 'Name': 'Plan',", "'Id': 4, 'Name': 'Plan',")]
    [InlineData("'Name': 'Plan', 'UnderlyingType': 'UInt8'", "'Name': 'Plan', 'UnderlyingType': 'UInt16'")]
    [InlineData("'Flags': true", "'Flags': false")]
    [InlineData("{ 'Name': 'Free', 'Value': 0 },", "{ 'Name': 'Free', 'Value': 0 }, { 'Name': 'Trial', 'Value': 2 },")]
    [InlineData("{ 'Name': 'Free', 'Value': 0 },", "{ 'Name': 'Free', 'Value': 2 },")]
    [InlineData("'Enums': [", "'Enums': [ { 'Id': 4, 'Name': 'Color', 'Members': [ { 'Name': 'Red', 'Value': 0 } ] },")]
    // An entity type: its identity, its primary key.
    [InlineData("'Id': 1, 'Name': 'Account',", "'Id': 3, 'Name': 'Account',")]
    [InlineData("'PrimaryKey': [1],", "'PrimaryKey': [2],")]
    [InlineData("'EntityTypes': [", "'EntityTypes': [ { 'Id': 3, 'Name': 'Tag', 'Fields': [ { 'Id': 1, 'Name': 'Id', 'Type': 'String' } ], 'PrimaryKey': [1] },")]
    // A field: its identity, type, nullability, comparison, string storage and enum type.
    [InlineData("{ 'Id': 4, 'Name': 'Subscription',", "{ 'Id': 7, 'Name': 'Subscription',")]
    [InlineData("'Name': 'Age', 'Type': 'Int32'", "'Name': 'Age', 'Type': 'Int64'")]
    [InlineData("'Name': 'Age', 'Type': 'Int32', 'Nullable': true", "'Name': 'Age', 'Type': 'Int32'")]
    [InlineData("'Comparison': 'Ordinal',", "'Comparison': 'OrdinalIgnoreCase',")]
    [InlineData("'StringStorage': 'Dictionary'", "'StringStorage': 'Plain'")]
    [InlineData("'Enum': 'Plan'", "'Enum': 'Tier'")]
    [InlineData("{ 'Id': 5, 'Name': 'Permissions', 'Type': 'Enum', 'Enum': 'Permission', 'Nullable': true },", "")]
    [InlineData("{ 'Id': 2, 'Name': 'Text', 'Type': 'String' }", "{ 'Id': 2, 'Name': 'Text', 'Type': 'String' }, { 'Id': 3, 'Name': 'Pinned', 'Type': 'Boolean' }")]
    // An index: its identity, its fields and their order, its Unique mark.
    [InlineData("{ 'Id': 1, 'Name': 'ByLogin',", "{ 'Id': 3, 'Name': 'ByLogin',")]
    [InlineData("'Fields': [3, 6]", "'Fields': [3, 4]")]
    [InlineData("'Fields': [3, 6]", "'Fields': [6, 3]")]
    [InlineData("'Fields': [2], 'Unique': true", "'Fields': [2]")]
    [InlineData("'Fields': [3, 6] }", "'Fields': [3, 6], 'Unique': true }")]
    [InlineData("{ 'Id': 1, 'Name': 'ByLogin', 'Fields': [2], 'Unique': true },", "")]
    [InlineData("{ 'Id': 2, 'Name': 'ByRegionAndAge', 'Fields': [3, 6] }", "{ 'Id': 2, 'Name': 'ByRegionAndAge', 'Fields': [3, 6] }, { 'Id': 3, 'Name': 'BySubscription', 'Fields': [4] }")]
    public void Fingerprint_changes_when_a_declaration_it_covers_changes(string part, string replacement)
    {
        var original = FingerprintDescriptor.Compile();
        var changed = FingerprintDescriptor.CompileWith(part, replacement);

        Assert.NotEqual(original.Fingerprint, changed.Fingerprint);
    }

    [Theory]
    // A rename of an entity type, a field, an index or an enum member.
    [InlineData("'Name': 'Account',", "'Name': 'Customer',")]
    [InlineData("'Name': 'Login',", "'Name': 'UserName',")]
    [InlineData("'Name': 'ByLogin',", "'Name': 'ByUserName',")]
    [InlineData("{ 'Name': 'Free', 'Value': 0 },", "{ 'Name': 'Basic', 'Value': 0 },")]
    // Another name for a value, one name less, another canonical name: the values stay.
    [InlineData("{ 'Name': 'Free', 'Value': 0 },", "{ 'Name': 'Free', 'Value': 0, 'Canonical': true }, { 'Name': 'Gratis', 'Value': 0 },")]
    [InlineData("{ 'Name': 'Paid', 'Value': 1, 'Canonical': true }, { 'Name': 'Premium', 'Value': 1 }", "{ 'Name': 'Paid', 'Value': 1 }")]
    [InlineData("{ 'Name': 'Paid', 'Value': 1, 'Canonical': true }, { 'Name': 'Premium', 'Value': 1 }", "{ 'Name': 'Paid', 'Value': 1 }, { 'Name': 'Premium', 'Value': 1, 'Canonical': true }")]
    // The .NET class.
    [InlineData("'ClrType': 'Example.Account'", "'ClrType': 'Other.Customer'")]
    [InlineData("'Name': 'Account', 'ClrType': 'Example.Account',", "'Name': 'Account',")]
    // The catalog version.
    [InlineData("'SchemaVersion': 1,", "'SchemaVersion': 7,")]
    // A default stated or left out.
    [InlineData("'StringEncoding': 'Utf8',", "")]
    [InlineData(", 'Comparison': 'Ordinal', 'StringStorage': 'Plain'", "")]
    [InlineData(", 'Flags': false", "")]
    [InlineData("'Name': 'Permission', 'UnderlyingType': 'Int32',", "'Name': 'Permission',")]
    [InlineData("'Name': 'Tier', 'UnderlyingType': 'UInt8',", "'Name': 'Tier', 'UnderlyingType': 'UInt8', 'Flags': false,")]
    [InlineData("{ 'Id': 2, 'Name': 'Text', 'Type': 'String' }", "{ 'Id': 2, 'Name': 'Text', 'Type': 'String', 'Nullable': false, 'Comparison': 'Ordinal', 'StringStorage': 'Plain' }")]
    [InlineData("'Fields': [3, 6] }", "'Fields': [3, 6], 'Unique': false }")]
    public void Fingerprint_does_not_change_with_names_classes_the_version_or_stated_defaults(string part, string replacement)
    {
        var original = FingerprintDescriptor.Compile();
        var changed = FingerprintDescriptor.CompileWith(part, replacement);

        Assert.Equal(original.Fingerprint, changed.Fingerprint);
    }

    [Theory]
    // The enum type, the entity type, the field, the index.
    [InlineData("{ 'Id': 1, 'Name': 'Spare',", "{ 'Id': 2, 'Name': 'Spare',")]
    [InlineData("'Id': 1, 'Name': 'Item',", "'Id': 2, 'Name': 'Item',")]
    [InlineData("{ 'Id': 3, 'Name': 'Note',", "{ 'Id': 4, 'Name': 'Note',")]
    [InlineData("{ 'Id': 1, 'Name': 'ByLabel',", "{ 'Id': 2, 'Name': 'ByLabel',")]
    public void Fingerprint_changes_when_an_identity_changes_and_nothing_else(string part, string replacement)
    {
        // In this schema a new identity moves nothing and is named by nothing.
        var original = LatticeSchema.Compile(FingerprintDescriptor.SingleIdentitiesJson);
        var changed = LatticeSchema.Compile(Descriptor.With(FingerprintDescriptor.SingleIdentitiesJson, part, replacement));

        Assert.NotEqual(original.Fingerprint, changed.Fingerprint);
    }

    [Fact]
    public void Renaming_an_enum_type_does_not_change_the_fingerprint()
    {
        // The enum type and the field that names it.
        var renamed = Descriptor.With(FingerprintDescriptor.Json, "'Name': 'Plan', 'UnderlyingType'", "'Name': 'Tariff', 'UnderlyingType'");
        renamed = Descriptor.With(renamed, "'Enum': 'Plan'", "'Enum': 'Tariff'");

        Assert.Equal(FingerprintDescriptor.Compile().Fingerprint, LatticeSchema.Compile(renamed).Fingerprint);
    }

    [Fact]
    public void Order_in_which_the_descriptor_lists_declarations_does_not_change_the_fingerprint()
    {
        Assert.Equal(FingerprintDescriptor.Compile().Fingerprint, FingerprintDescriptor.CompileReversed().Fingerprint);
    }

    [Fact]
    public void Formatting_of_the_descriptor_does_not_change_the_fingerprint()
    {
        var compact = JsonNode.Parse(FingerprintDescriptor.Json)!.ToJsonString();

        Assert.Equal(FingerprintDescriptor.Compile().Fingerprint, LatticeSchema.Compile(compact).Fingerprint);
    }

    [Fact]
    public void Fingerprint_of_a_fixed_descriptor_is_the_recorded_one_on_every_platform()
    {
        var schema = FingerprintDescriptor.Compile();

        Assert.Equal(RecordedFingerprint, schema.Fingerprint.ToString());
    }
}
