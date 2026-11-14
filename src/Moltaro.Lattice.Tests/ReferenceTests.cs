namespace Moltaro.Lattice.Tests;

// A reference names an entity type of the schema and holds the primary key of an entity of
// it, in the .NET type of that key. It does not own its target; it may refer to its own
// entity type. A record holds the key and accepts the key.
public sealed class ReferenceTests
{
    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Reference_names_its_entity_type_and_holds_the_key_of_its_target()
    {
        var schema = ValueSchema.Compile();

        // A reference to the entity type that declares it, whose key is an Int64.
        var manager = ValueSchema.Field(schema, "Manager").Declaration;
        Assert.Equal(LatticeFieldTypeEnum.Reference, manager.Type);
        Assert.Equal("User", manager.ReferencedEntityTypeName);
        Assert.True(manager.IsNullable);
        Assert.Equal(typeof(long), manager.ValueType);
        Assert.Null(manager.OwnedType);
        Assert.Null(manager.EnumType);
        Assert.Null(manager.Comparison);

        // A reference to an entity type whose key is a Guid, from an owned type.
        Assert.True(ValueSchema.OwnedType(schema, "Address").TryGetField("Owner", out var owner));
        Assert.Equal("Group", owner.Declaration.ReferencedEntityTypeName);
        Assert.Equal(typeof(Guid), owner.Declaration.ValueType);

        // The name it holds is the name of a declared entity type.
        Assert.True(schema.TryGetEntityType(owner.Declaration.ReferencedEntityTypeName!, out var group));
        Assert.Equal(typeof(Guid), group.Key.Declaration.ValueType);
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    [InlineData("Int8", typeof(sbyte))]
    [InlineData("UInt8", typeof(byte))]
    [InlineData("Int16", typeof(short))]
    [InlineData("UInt16", typeof(ushort))]
    [InlineData("Int32", typeof(int))]
    [InlineData("UInt32", typeof(uint))]
    [InlineData("Int64", typeof(long))]
    [InlineData("UInt64", typeof(ulong))]
    [InlineData("String", typeof(string))]
    [InlineData("Guid", typeof(Guid))]
    public void Reference_holds_the_key_in_the_type_of_the_key_of_its_target(string keyType, Type valueType)
    {
        var schema = LatticeSchema.Compile($$"""
            {
              "EntityTypes": [
                { "Id": 1, "Name": "Target", "Fields": [ { "Id": 1, "Name": "Key", "Type": "{{keyType}}" } ], "PrimaryKey": [1] },
                {
                  "Id": 2, "Name": "Holder",
                  "Fields": [
                    { "Id": 1, "Name": "Id", "Type": "Int64" },
                    { "Id": 2, "Name": "Target", "Type": "Reference", "EntityType": "Target" },
                    { "Id": 3, "Name": "Targets", "Type": "Set", "Element": { "Type": "Reference", "EntityType": "Target" } }
                  ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);

        Assert.True(schema.TryGetEntityType("Holder", out var holder));
        Assert.True(holder.TryGetField("Target", out var target));
        Assert.Equal(valueType, target.Declaration.ValueType);
        Assert.True(holder.TryGetField("Targets", out var targets));
        Assert.Equal(valueType, targets.Declaration.Element!.ValueType);
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    // An entity type the schema does not declare, an owned type, which is not an entity type,
    // and no entity type at all.
    [InlineData("{ 'Id': 5, 'Name': 'Manager', 'Type': 'Reference', 'EntityType': 'User', 'Nullable': true }", "{ 'Id': 5, 'Name': 'Manager', 'Type': 'Reference', 'EntityType': 'Customer', 'Nullable': true }", "User", "Manager")]
    [InlineData("{ 'Id': 5, 'Name': 'Manager', 'Type': 'Reference', 'EntityType': 'User', 'Nullable': true }", "{ 'Id': 5, 'Name': 'Manager', 'Type': 'Reference', 'EntityType': 'Address', 'Nullable': true }", "User", "Manager")]
    [InlineData("{ 'Id': 5, 'Name': 'Manager', 'Type': 'Reference', 'EntityType': 'User', 'Nullable': true }", "{ 'Id': 5, 'Name': 'Manager', 'Type': 'Reference', 'Nullable': true }", "User", "Manager")]
    // The same as an element and inside an owned type.
    [InlineData("'Element': { 'Type': 'Reference', 'EntityType': 'Group' }, 'Nullable': true", "'Element': { 'Type': 'Reference', 'EntityType': 'Team' }, 'Nullable': true", "User", "Teams")]
    [InlineData("'Name': 'Owner', 'Type': 'Reference', 'EntityType': 'Group'", "'Name': 'Owner', 'Type': 'Reference', 'EntityType': 'Team'", "Address", "Owner")]
    // An entity type named by a field that is not a reference.
    [InlineData("{ 'Id': 20, 'Name': 'Login', 'Type': 'String' }", "{ 'Id': 20, 'Name': 'Login', 'Type': 'String', 'EntityType': 'Group' }", "User", "Login")]
    [InlineData("'Name': 'Groups', 'Type': 'List',", "'Name': 'Groups', 'Type': 'List', 'EntityType': 'Group',", "User", "Groups")]
    public void Reference_names_a_declared_entity_type_and_nothing_else_names_one(string part, string replacement, params string[] place)
    {
        var exception = Descriptor.ExpectRefused(ValueSchema.Json, part, replacement);

        Assert.All(place, name => Assert.Contains(name, exception.Message));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Record_holds_a_reference_as_the_key_of_its_target()
    {
        var user = ValueSchema.User(ValueSchema.Compile());
        var groupKey = Guid.NewGuid();

        var record = new LatticeRecord(user)
        {
            ["Manager"] = 7L,
            ["Groups"] = new[] { groupKey, groupKey },
        };

        Assert.Equal(7L, record["Manager"]);
        Assert.Equal([groupKey, groupKey], Assert.IsType<List<Guid>>(record["Groups"]));

        // A reference is its key; nothing else stands for it.
        Assert.Throws<ArgumentException>(() => record["Manager"] = "seven");
        Assert.Throws<ArgumentException>(() => record["Manager"] = groupKey);
        Assert.Throws<ArgumentException>(() => record["Groups"] = new[] { 1L, 2L });

        // A nullable reference holds no key.
        record["Manager"] = null;
        Assert.Null(record["Manager"]);
    }
}
