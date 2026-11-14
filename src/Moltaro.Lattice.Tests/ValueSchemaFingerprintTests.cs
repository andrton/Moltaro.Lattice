namespace Moltaro.Lattice.Tests;

// The fingerprint of a schema covers every owned type with its identity and its fields, held
// or not; of a field the owned type of an owned value, the entity type a reference refers to
// and the declarations of the element, the key and the value of a collection, comparisons
// included; and of an index the path of every part. Names, classes and the order of the
// descriptor are not part of it.
public sealed class ValueSchemaFingerprintTests
{
    [Theory(Skip = NotImplemented.SchemaValues)]
    // An owned type: its identity, a field more, held or not; another owned type.
    [InlineData("'Id': 1, 'Name': 'Address',", "'Id': 4, 'Name': 'Address',")]
    [InlineData("{ 'Id': 3, 'Name': 'Geo', 'Type': 'Owned', 'OwnedType': 'Point', 'Nullable': true }", "{ 'Id': 3, 'Name': 'Geo', 'Type': 'Owned', 'OwnedType': 'Point', 'Nullable': true }, { 'Id': 4, 'Name': 'Floor', 'Type': 'Int16' }")]
    [InlineData("{ 'Id': 1, 'Name': 'Memo', 'Type': 'String' }", "{ 'Id': 1, 'Name': 'Memo', 'Type': 'String' }, { 'Id': 2, 'Name': 'Pinned', 'Type': 'Boolean' }")]
    [InlineData("'OwnedTypes': [", "'OwnedTypes': [ { 'Id': 4, 'Name': 'Extra', 'Fields': [ { 'Id': 1, 'Name': 'Flag', 'Type': 'Boolean' } ] },")]
    // A field of an owned type: its identity, type, nullability, comparison, string storage.
    [InlineData("{ 'Id': 2, 'Name': 'Zip',", "{ 'Id': 4, 'Name': 'Zip',")]
    [InlineData("'Name': 'Zip', 'Type': 'String', 'Nullable': true", "'Name': 'Zip', 'Type': 'Int32', 'Nullable': true")]
    [InlineData("'Name': 'Zip', 'Type': 'String', 'Nullable': true", "'Name': 'Zip', 'Type': 'String'")]
    [InlineData("'Name': 'City', 'Type': 'String', 'Comparison': 'Ordinal'", "'Name': 'City', 'Type': 'String', 'Comparison': 'OrdinalIgnoreCase'")]
    [InlineData("'Name': 'City', 'Type': 'String', 'Comparison': 'Ordinal'", "'Name': 'City', 'Type': 'String', 'Comparison': 'Ordinal', 'StringStorage': 'Dictionary'")]
    // The owned type an owned value holds.
    [InlineData("'Name': 'Home', 'Type': 'Owned', 'OwnedType': 'Address'", "'Name': 'Home', 'Type': 'Owned', 'OwnedType': 'Point'")]
    // The entity type a reference refers to, also one with the same key type.
    [InlineData("'Element': { 'Type': 'Reference', 'EntityType': 'Team' }", "'Element': { 'Type': 'Reference', 'EntityType': 'Club' }")]
    [InlineData("'Name': 'Manager', 'Type': 'Reference', 'EntityType': 'Person'", "'Name': 'Manager', 'Type': 'Reference', 'EntityType': 'Team'")]
    // The declaration of an element, a key or a value: type, nullability, comparison, string
    // storage, enum type; and the kind of collection.
    [InlineData("'Value': { 'Type': 'Int32', 'Nullable': true }", "'Value': { 'Type': 'Int64', 'Nullable': true }")]
    [InlineData("'Value': { 'Type': 'Int32', 'Nullable': true }", "'Value': { 'Type': 'Int32' }")]
    [InlineData("'Key': { 'Type': 'String', 'Comparison': 'Ordinal' }", "'Key': { 'Type': 'String', 'Comparison': 'OrdinalIgnoreCase' }")]
    [InlineData("'Element': { 'Type': 'String', 'StringStorage': 'Plain' }", "'Element': { 'Type': 'String', 'StringStorage': 'Dictionary' }")]
    [InlineData("'Element': { 'Type': 'Enum', 'Enum': 'Color' }", "'Element': { 'Type': 'Enum', 'Enum': 'Shade' }")]
    [InlineData("'Name': 'Teams', 'Type': 'List',", "'Name': 'Teams', 'Type': 'Set',")]
    // The path of a part of an index: through another owned value, to another field, a step
    // more; and a field of the entity type in place of a path.
    [InlineData("'Fields': [ [2, 1] ]", "'Fields': [ [3, 1] ]")]
    [InlineData("'Fields': [ [2, 1] ]", "'Fields': [ [2, 2] ]")]
    [InlineData("'Fields': [ [2, 1] ]", "'Fields': [ [2, 3, 1] ]")]
    [InlineData("'Fields': [ [2, 1] ]", "'Fields': [ 9 ]")]
    public void Fingerprint_changes_when_a_declaration_of_owned_types_collections_references_or_index_paths_changes(string part, string replacement)
    {
        var original = ValueFingerprintDescriptor.Compile();
        var changed = ValueFingerprintDescriptor.CompileWith(part, replacement);

        Assert.NotEqual(original.Fingerprint, changed.Fingerprint);
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    // The .NET class of an owned type.
    [InlineData("'ClrType': 'Example.Address',", "'ClrType': 'Other.Location',")]
    [InlineData("'Name': 'Address', 'ClrType': 'Example.Address',", "'Name': 'Address',")]
    // A default stated or left out inside a collection.
    [InlineData("'Key': { 'Type': 'String', 'Comparison': 'Ordinal' }", "'Key': { 'Type': 'String' }")]
    [InlineData("'Element': { 'Type': 'String', 'StringStorage': 'Plain' }", "'Element': { 'Type': 'String' }")]
    [InlineData("'Element': { 'Type': 'Reference', 'EntityType': 'Team' }", "'Element': { 'Type': 'Reference', 'EntityType': 'Team', 'Nullable': false }")]
    [InlineData("'Name': 'City', 'Type': 'String', 'Comparison': 'Ordinal'", "'Name': 'City', 'Type': 'String'")]
    public void Fingerprint_does_not_change_with_the_class_of_an_owned_type_or_stated_defaults(string part, string replacement)
    {
        var original = ValueFingerprintDescriptor.Compile();
        var changed = ValueFingerprintDescriptor.CompileWith(part, replacement);

        Assert.Equal(original.Fingerprint, changed.Fingerprint);
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    // An owned type with the fields that hold it, a field of an owned type, an entity type that
    // is referred to with the references to it.
    [InlineData("Address", "Location")]
    [InlineData("Point", "Coordinates")]
    [InlineData("City", "Town")]
    [InlineData("Team", "Squad")]
    public void Fingerprint_does_not_change_on_a_rename(string name, string newName)
    {
        var renamed = ValueFingerprintDescriptor.CompileRenamed(name, newName);

        Assert.Equal(ValueFingerprintDescriptor.Compile().Fingerprint, renamed.Fingerprint);
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    // The owned type Spare and its field, which nothing names.
    [InlineData("'Id': 3, 'Name': 'Spare',", "'Id': 5, 'Name': 'Spare',")]
    [InlineData("{ 'Id': 1, 'Name': 'Memo',", "{ 'Id': 2, 'Name': 'Memo',")]
    public void Fingerprint_changes_when_the_identity_of_an_owned_type_or_of_its_field_changes_and_nothing_else(string part, string replacement)
    {
        var original = ValueFingerprintDescriptor.Compile();
        var changed = ValueFingerprintDescriptor.CompileWith(part, replacement);

        Assert.NotEqual(original.Fingerprint, changed.Fingerprint);

        // Nothing that Person stores changed.
        Assert.Equal(ValueFingerprintDescriptor.Person(original).LayoutFingerprint, ValueFingerprintDescriptor.Person(changed).LayoutFingerprint);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Order_in_which_the_descriptor_lists_owned_types_and_fields_does_not_change_the_fingerprint()
    {
        Assert.Equal(ValueFingerprintDescriptor.Compile().Fingerprint, ValueFingerprintDescriptor.CompileReversed().Fingerprint);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Same_declarations_with_owned_types_and_collections_give_the_same_fingerprint()
    {
        Assert.Equal(ValueFingerprintDescriptor.Compile().Fingerprint, ValueFingerprintDescriptor.Compile().Fingerprint);
    }
}
