namespace Moltaro.Lattice.Tests;

// The layout fingerprint of an entity type covers everything an entity of that type stores.
// For an owned value it covers the identity of the owned type and every field of it, and so on
// through the owned values inside it; for a List, a Map or a Set the declarations of the
// element, the key and the value; for a reference the type of the key of its target and
// nothing else of the target. It covers which field is the primary key, and not the identity
// of the entity type. Comparisons, indexes, names and classes are not part of it.
public sealed class ValueLayoutFingerprintTests
{
    [Theory(Skip = NotImplemented.SchemaValues)]
    // A field added to an owned type the entity type holds, directly or inside another one.
    [InlineData("{ 'Id': 3, 'Name': 'Geo', 'Type': 'Owned', 'OwnedType': 'Point', 'Nullable': true }", "{ 'Id': 3, 'Name': 'Geo', 'Type': 'Owned', 'OwnedType': 'Point', 'Nullable': true }, { 'Id': 4, 'Name': 'Floor', 'Type': 'Int16' }")]
    [InlineData("{ 'Id': 2, 'Name': 'Lon', 'Type': 'Float64' }", "{ 'Id': 2, 'Name': 'Lon', 'Type': 'Float64' }, { 'Id': 3, 'Name': 'Alt', 'Type': 'Float64' }")]
    // A field of an owned type: its identity, type, nullability, string storage.
    [InlineData("{ 'Id': 2, 'Name': 'Zip',", "{ 'Id': 4, 'Name': 'Zip',")]
    [InlineData("'Name': 'Zip', 'Type': 'String', 'Nullable': true", "'Name': 'Zip', 'Type': 'Int32', 'Nullable': true")]
    [InlineData("'Name': 'Zip', 'Type': 'String', 'Nullable': true", "'Name': 'Zip', 'Type': 'String'")]
    [InlineData("'Name': 'City', 'Type': 'String', 'Comparison': 'Ordinal'", "'Name': 'City', 'Type': 'String', 'Comparison': 'Ordinal', 'StringStorage': 'Dictionary'")]
    [InlineData("'Name': 'Geo', 'Type': 'Owned', 'OwnedType': 'Point', 'Nullable': true", "'Name': 'Geo', 'Type': 'Owned', 'OwnedType': 'Point'")]
    // The identity of an owned type the entity type holds, directly or inside another one, and
    // another owned type held by a field.
    [InlineData("'Id': 1, 'Name': 'Address',", "'Id': 4, 'Name': 'Address',")]
    [InlineData("'Id': 2, 'Name': 'Point',", "'Id': 5, 'Name': 'Point',")]
    [InlineData("'Name': 'Home', 'Type': 'Owned', 'OwnedType': 'Address'", "'Name': 'Home', 'Type': 'Owned', 'OwnedType': 'Point'")]
    // The declaration of an element, a key or a value: its type, nullability, string storage,
    // the underlying type of its enum type; and the kind of collection.
    [InlineData("'Element': { 'Type': 'Reference', 'EntityType': 'Team' }", "'Element': { 'Type': 'Reference', 'EntityType': 'Team', 'Nullable': true }")]
    [InlineData("'Element': { 'Type': 'Reference', 'EntityType': 'Team' }", "'Element': { 'Type': 'Guid' }")]
    [InlineData("'Value': { 'Type': 'Int32', 'Nullable': true }", "'Value': { 'Type': 'Int64', 'Nullable': true }")]
    [InlineData("'Value': { 'Type': 'Int32', 'Nullable': true }", "'Value': { 'Type': 'Int32' }")]
    [InlineData("'Key': { 'Type': 'String', 'Comparison': 'Ordinal' }", "'Key': { 'Type': 'Int32' }")]
    [InlineData("'Element': { 'Type': 'String', 'StringStorage': 'Plain' }", "'Element': { 'Type': 'String', 'StringStorage': 'Dictionary' }")]
    [InlineData("'Name': 'Color', 'UnderlyingType': 'UInt8'", "'Name': 'Color', 'UnderlyingType': 'UInt16'")]
    [InlineData("'Name': 'Teams', 'Type': 'List',", "'Name': 'Teams', 'Type': 'Set',")]
    // The key type of an entity type a reference refers to, and a reference to an entity type
    // with another key type.
    [InlineData("{ 'Id': 1, 'Name': 'TeamId', 'Type': 'Guid' }", "{ 'Id': 1, 'Name': 'TeamId', 'Type': 'String' }")]
    [InlineData("'Name': 'Manager', 'Type': 'Reference', 'EntityType': 'Person'", "'Name': 'Manager', 'Type': 'Reference', 'EntityType': 'Team'")]
    // Which field is the primary key.
    [InlineData("'PrimaryKey': [1],", "'PrimaryKey': [9],")]
    public void Layout_fingerprint_changes_when_what_the_entity_type_stores_changes(string part, string replacement)
    {
        var original = ValueFingerprintDescriptor.Person(ValueFingerprintDescriptor.Compile());
        var changed = ValueFingerprintDescriptor.Person(ValueFingerprintDescriptor.CompileWith(part, replacement));

        Assert.NotEqual(original.LayoutFingerprint, changed.LayoutFingerprint);
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    // An owned type the entity type does not hold, and a new one.
    [InlineData("{ 'Id': 1, 'Name': 'Memo', 'Type': 'String' }", "{ 'Id': 1, 'Name': 'Memo', 'Type': 'String' }, { 'Id': 2, 'Name': 'Pinned', 'Type': 'Boolean' }")]
    [InlineData("'OwnedTypes': [", "'OwnedTypes': [ { 'Id': 4, 'Name': 'Extra', 'Fields': [ { 'Id': 1, 'Name': 'Flag', 'Type': 'Boolean' } ] },")]
    // An entity type a reference refers to, in everything but its key type: another field, its
    // identity; and a reference to another entity type with the same key type.
    [InlineData("{ 'Id': 2, 'Name': 'Title', 'Type': 'String' }", "{ 'Id': 2, 'Name': 'Title', 'Type': 'Int32' }")]
    [InlineData("'Id': 2, 'Name': 'Team',", "'Id': 4, 'Name': 'Team',")]
    [InlineData("'Element': { 'Type': 'Reference', 'EntityType': 'Team' }", "'Element': { 'Type': 'Reference', 'EntityType': 'Club' }")]
    // A comparison of a key and of a field inside an owned value.
    [InlineData("'Key': { 'Type': 'String', 'Comparison': 'Ordinal' }", "'Key': { 'Type': 'String', 'Comparison': 'OrdinalIgnoreCase' }")]
    [InlineData("'Name': 'City', 'Type': 'String', 'Comparison': 'Ordinal'", "'Name': 'City', 'Type': 'String', 'Comparison': 'OrdinalIgnoreCase'")]
    // An index on a field inside an owned value: another path, removed, added.
    [InlineData("'Fields': [ [2, 1] ]", "'Fields': [ [3, 1] ]")]
    [InlineData("{ 'Id': 1, 'Name': 'ByHomeCity', 'Fields': [ [2, 1] ] },", "")]
    [InlineData("{ 'Id': 2, 'Name': 'ByBadge', 'Fields': [9] }", "{ 'Id': 2, 'Name': 'ByBadge', 'Fields': [9] }, { 'Id': 3, 'Name': 'ByWorkZip', 'Fields': [ [3, 2] ] }")]
    // The .NET class of an owned type, and the identity of the entity type itself.
    [InlineData("'ClrType': 'Example.Address',", "'ClrType': 'Other.Location',")]
    [InlineData("'Name': 'Address', 'ClrType': 'Example.Address',", "'Name': 'Address',")]
    [InlineData("'Id': 1, 'Name': 'Person',", "'Id': 7, 'Name': 'Person',")]
    // An enum type in a way that does not change how its values are stored.
    [InlineData("'Element': { 'Type': 'Enum', 'Enum': 'Color' }", "'Element': { 'Type': 'Enum', 'Enum': 'Shade' }")]
    [InlineData("{ 'Name': 'Green', 'Value': 1 }", "{ 'Name': 'Green', 'Value': 1 }, { 'Name': 'Blue', 'Value': 2 }")]
    // A default stated or left out inside a collection.
    [InlineData("'Key': { 'Type': 'String', 'Comparison': 'Ordinal' }", "'Key': { 'Type': 'String' }")]
    [InlineData("'Element': { 'Type': 'String', 'StringStorage': 'Plain' }", "'Element': { 'Type': 'String' }")]
    [InlineData("'Element': { 'Type': 'Reference', 'EntityType': 'Team' }", "'Element': { 'Type': 'Reference', 'EntityType': 'Team', 'Nullable': false }")]
    public void Layout_fingerprint_does_not_change_with_what_the_entity_type_does_not_store(string part, string replacement)
    {
        var original = ValueFingerprintDescriptor.Person(ValueFingerprintDescriptor.Compile());
        var changed = ValueFingerprintDescriptor.Person(ValueFingerprintDescriptor.CompileWith(part, replacement));

        Assert.Equal(original.LayoutFingerprint, changed.LayoutFingerprint);
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    // An owned type with the fields that hold it, an owned field, an entity type that is
    // referred to with the references to it, and the entity type itself.
    [InlineData("Address", "Location")]
    [InlineData("City", "Town")]
    [InlineData("Team", "Squad")]
    [InlineData("Person", "Customer")]
    public void Layout_fingerprint_does_not_change_on_a_rename(string name, string newName)
    {
        var original = ValueFingerprintDescriptor.Person(ValueFingerprintDescriptor.Compile());
        var renamed = ValueFingerprintDescriptor.Person(ValueFingerprintDescriptor.CompileRenamed(name, newName));

        Assert.Equal(original.LayoutFingerprint, renamed.LayoutFingerprint);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Order_in_which_the_descriptor_lists_owned_types_and_fields_does_not_change_the_layout_fingerprint()
    {
        var original = ValueFingerprintDescriptor.Compile();
        var reversed = ValueFingerprintDescriptor.CompileReversed();

        Assert.Equal(original.EntityTypes.OrderBy(entityType => entityType.Id).Select(entityType => entityType.LayoutFingerprint), reversed.EntityTypes.OrderBy(entityType => entityType.Id).Select(entityType => entityType.LayoutFingerprint));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Layout_fingerprint_changes_when_another_field_becomes_the_primary_key()
    {
        // In the descriptor of the tests of Epic 1, sprint 2: Login, a String, becomes the key
        // of Account in place of Id.
        var original = FingerprintDescriptor.Compile();
        var changed = FingerprintDescriptor.CompileWith("'PrimaryKey': [1],", "'PrimaryKey': [2],");

        Assert.NotEqual(Account(original).LayoutFingerprint, Account(changed).LayoutFingerprint);
    }

    [Fact]
    public void Entity_types_that_differ_only_in_identity_and_name_have_the_same_layout_fingerprint()
    {
        var schema = LatticeSchema.Compile("""
            {
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Draft",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Guid" }, { "Id": 2, "Name": "Text", "Type": "String", "Nullable": true } ],
                  "PrimaryKey": [1]
                },
                {
                  "Id": 2, "Name": "Published",
                  "Fields": [ { "Id": 1, "Name": "Key", "Type": "Guid" }, { "Id": 2, "Name": "Body", "Type": "String", "Nullable": true } ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);

        Assert.Equal(schema.EntityTypes[0].LayoutFingerprint, schema.EntityTypes[1].LayoutFingerprint);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Entity_types_with_the_same_nested_values_and_key_have_the_same_layout_fingerprint()
    {
        // A second Person with another identity and another name, and the same fields.
        var json = Descriptor.With(
            ValueFingerprintDescriptor.Json,
            "'EntityTypes': [",
            "'EntityTypes': [ { 'Id': 9, 'Name': 'Twin', 'Fields': [ { 'Id': 1, 'Name': 'Id', 'Type': 'Int64' }, { 'Id': 2, 'Name': 'Home', 'Type': 'Owned', 'OwnedType': 'Address', 'Nullable': true }, { 'Id': 3, 'Name': 'Work', 'Type': 'Owned', 'OwnedType': 'Address' }, { 'Id': 4, 'Name': 'Manager', 'Type': 'Reference', 'EntityType': 'Person', 'Nullable': true }, { 'Id': 5, 'Name': 'Teams', 'Type': 'List', 'Element': { 'Type': 'Reference', 'EntityType': 'Team' } }, { 'Id': 6, 'Name': 'Scores', 'Type': 'Map', 'Key': { 'Type': 'String', 'Comparison': 'Ordinal' }, 'Value': { 'Type': 'Int32', 'Nullable': true } }, { 'Id': 7, 'Name': 'Tags', 'Type': 'Set', 'Element': { 'Type': 'String', 'StringStorage': 'Plain' } }, { 'Id': 8, 'Name': 'Paints', 'Type': 'List', 'Element': { 'Type': 'Enum', 'Enum': 'Color' } }, { 'Id': 9, 'Name': 'Badge', 'Type': 'String' } ], 'PrimaryKey': [1] },");
        var schema = LatticeSchema.Compile(json);

        var twin = Assert.Single(schema.EntityTypes, entityType => entityType.Id == 9);
        Assert.Equal(ValueFingerprintDescriptor.Person(schema).LayoutFingerprint, twin.LayoutFingerprint);
    }

    private static LatticeEntityType Account(LatticeSchema schema) => Assert.Single(schema.EntityTypes, entityType => entityType.Id == 1);
}
