namespace Moltaro.Lattice.Tests;

// The layout fingerprint of an entity type covers only how one entity of that type is stored:
// the string encoding and, for every field, its identity, type, nullability, string storage
// and the underlying type of its enum type. Comparisons, indexes, names and other entity types
// are not part of it. A migration converts the entity types whose layout fingerprint differs.
public sealed class LayoutFingerprintTests
{
    // The layout fingerprint of Account in FingerprintDescriptor.Json. The test author records
    // it from the first implementation, and it is never changed afterwards: this test runs on
    // Windows and on Linux, and passing on both shows that the fingerprint does not depend on
    // the platform.
    private const string RecordedAccountLayoutFingerprint = "not recorded yet: the test author records it from the first implementation";

    [Theory]
    // The string encoding.
    [InlineData("'StringEncoding': 'Utf8',", "'StringEncoding': 'Utf16',")]
    // A field: its identity, type, nullability, string storage.
    [InlineData("{ 'Id': 4, 'Name': 'Subscription',", "{ 'Id': 7, 'Name': 'Subscription',")]
    [InlineData("'Name': 'Age', 'Type': 'Int32'", "'Name': 'Age', 'Type': 'Int64'")]
    [InlineData("'Name': 'Age', 'Type': 'Int32', 'Nullable': true", "'Name': 'Age', 'Type': 'Int32'")]
    [InlineData("'StringStorage': 'Dictionary'", "'StringStorage': 'Plain'")]
    [InlineData("'Comparison': 'Ordinal', 'StringStorage': 'Plain'", "'Comparison': 'Ordinal', 'StringStorage': 'Dictionary'")]
    // The underlying type of the enum type of a field, in the enum type or by naming another.
    [InlineData("'Name': 'Plan', 'UnderlyingType': 'UInt8'", "'Name': 'Plan', 'UnderlyingType': 'UInt16'")]
    [InlineData("'Enum': 'Plan'", "'Enum': 'Permission'")]
    // A field more or less.
    [InlineData("{ 'Id': 6, 'Name': 'Age', 'Type': 'Int32', 'Nullable': true }", "{ 'Id': 6, 'Name': 'Age', 'Type': 'Int32', 'Nullable': true }, { 'Id': 7, 'Name': 'Pinned', 'Type': 'Boolean' }")]
    [InlineData("{ 'Id': 5, 'Name': 'Permissions', 'Type': 'Enum', 'Enum': 'Permission', 'Nullable': true },", "")]
    public void Layout_fingerprint_changes_when_a_stored_field_changes(string part, string replacement)
    {
        var original = Account(FingerprintDescriptor.Compile());
        var changed = Account(FingerprintDescriptor.CompileWith(part, replacement));

        Assert.NotEqual(original.LayoutFingerprint, changed.LayoutFingerprint);
    }

    [Theory]
    // A comparison.
    [InlineData("'Comparison': 'Ordinal',", "'Comparison': 'OrdinalIgnoreCase',")]
    // An index: added, removed, its identity, its fields, its Unique mark.
    [InlineData("{ 'Id': 2, 'Name': 'ByRegionAndAge', 'Fields': [3, 6] }", "{ 'Id': 2, 'Name': 'ByRegionAndAge', 'Fields': [3, 6] }, { 'Id': 3, 'Name': 'BySubscription', 'Fields': [4] }")]
    [InlineData("{ 'Id': 1, 'Name': 'ByLogin', 'Fields': [2], 'Unique': true },", "")]
    [InlineData("{ 'Id': 1, 'Name': 'ByLogin',", "{ 'Id': 3, 'Name': 'ByLogin',")]
    [InlineData("'Fields': [3, 6]", "'Fields': [6, 3]")]
    [InlineData("'Fields': [2], 'Unique': true", "'Fields': [2]")]
    // A name, the .NET class, the catalog version.
    [InlineData("'Name': 'Account',", "'Name': 'Customer',")]
    [InlineData("'Name': 'Login',", "'Name': 'UserName',")]
    [InlineData("'ClrType': 'Example.Account'", "'ClrType': 'Other.Customer'")]
    [InlineData("'SchemaVersion': 1,", "'SchemaVersion': 7,")]
    // An enum type in a way that does not change how its values are stored: a value more,
    // the Flags mark, another enum type with the same underlying type.
    [InlineData("{ 'Name': 'Free', 'Value': 0 },", "{ 'Name': 'Free', 'Value': 0 }, { 'Name': 'Trial', 'Value': 2 },")]
    [InlineData("'Flags': true", "'Flags': false")]
    [InlineData("'Enum': 'Plan'", "'Enum': 'Tier'")]
    // Another entity type.
    [InlineData("{ 'Id': 2, 'Name': 'Text', 'Type': 'String' }", "{ 'Id': 2, 'Name': 'Text', 'Type': 'String' }, { 'Id': 3, 'Name': 'Pinned', 'Type': 'Boolean' }")]
    [InlineData("'EntityTypes': [", "'EntityTypes': [ { 'Id': 3, 'Name': 'Tag', 'Fields': [ { 'Id': 1, 'Name': 'Id', 'Type': 'String' } ], 'PrimaryKey': [1] },")]
    // A default stated or left out.
    [InlineData(", 'Comparison': 'Ordinal', 'StringStorage': 'Plain'", "")]
    public void Layout_fingerprint_does_not_change_with_comparisons_indexes_names_or_other_entity_types(string part, string replacement)
    {
        var original = Account(FingerprintDescriptor.Compile());
        var changed = Account(FingerprintDescriptor.CompileWith(part, replacement));

        Assert.Equal(original.LayoutFingerprint, changed.LayoutFingerprint);
    }

    [Fact]
    public void Change_of_an_enum_type_reaches_the_layout_of_the_entity_types_that_store_it_only()
    {
        var original = FingerprintDescriptor.Compile();
        var changed = FingerprintDescriptor.CompileWith("'Name': 'Plan', 'UnderlyingType': 'UInt8'", "'Name': 'Plan', 'UnderlyingType': 'UInt16'");

        // Account has a field of Plan; Note has none.
        Assert.NotEqual(Account(original).LayoutFingerprint, Account(changed).LayoutFingerprint);
        Assert.Equal(Note(original).LayoutFingerprint, Note(changed).LayoutFingerprint);
    }

    [Fact]
    public void Change_of_one_entity_type_leaves_the_layout_of_the_others()
    {
        var original = FingerprintDescriptor.Compile();
        var changed = FingerprintDescriptor.CompileWith("'Name': 'Age', 'Type': 'Int32'", "'Name': 'Age', 'Type': 'Int64'");

        Assert.NotEqual(Account(original).LayoutFingerprint, Account(changed).LayoutFingerprint);
        Assert.Equal(Note(original).LayoutFingerprint, Note(changed).LayoutFingerprint);
    }

    [Fact]
    public void Order_in_which_the_descriptor_lists_declarations_does_not_change_the_layout_fingerprint()
    {
        var original = FingerprintDescriptor.Compile();
        var reversed = FingerprintDescriptor.CompileReversed();

        Assert.Equal(Account(original).LayoutFingerprint, Account(reversed).LayoutFingerprint);
        Assert.Equal(Note(original).LayoutFingerprint, Note(reversed).LayoutFingerprint);
    }

    [Fact]
    public void Comparison_changes_the_schema_fingerprint_and_not_the_layout_fingerprint()
    {
        // A comparison changes what the schema compares, not how an account is stored.
        var original = FingerprintDescriptor.Compile();
        var ignoringCase = FingerprintDescriptor.CompileWith("'Comparison': 'Ordinal',", "'Comparison': 'OrdinalIgnoreCase',");

        Assert.NotEqual(original.Fingerprint, ignoringCase.Fingerprint);
        Assert.Equal(Account(original).LayoutFingerprint, Account(ignoringCase).LayoutFingerprint);
    }

    [Fact(Skip = NotImplemented.SchemaDeclarations)]
    public void Layout_fingerprint_of_a_fixed_descriptor_is_the_recorded_one_on_every_platform()
    {
        var account = Account(FingerprintDescriptor.Compile());

        Assert.Equal(RecordedAccountLayoutFingerprint, account.LayoutFingerprint.ToString());
    }

    // The entity types are found by their identities, which no variant changes; one variant
    // renames Account.
    private static LatticeEntityType Account(LatticeSchema schema) => Assert.Single(schema.EntityTypes, entityType => entityType.Id == 1);

    private static LatticeEntityType Note(LatticeSchema schema) => Assert.Single(schema.EntityTypes, entityType => entityType.Id == 2);
}
