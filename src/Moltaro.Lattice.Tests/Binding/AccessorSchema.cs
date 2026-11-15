namespace Moltaro.Lattice.Tests.Binding;

// The schemas of the tests of the accessors of a property (SetAccessorTests, InheritedMemberTests)
// and what those tests expect of a class, on the implementation a test runs on.
//
// Note is an entity type with the key Id, an Int64, and the String field Text, bound to TNote.
// Holder is an entity type with the key Id and the field Value of the owned type Stamp, bound to
// BindingSchema.Holder<TStamp>; Stamp has the String field Text and the Int32 field Level and is
// bound to TStamp.
internal static class AccessorSchema
{
    public static LatticeSchema Note<TNote>() => LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note", "ClrType": "{{typeof(TNote).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    public static LatticeSchema Stamped<TStamp>() => LatticeSchema.Compile($$"""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Stamp", "ClrType": "{{typeof(TStamp).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Text", "Type": "String" }, { "Id": 2, "Name": "Level", "Type": "Int32" } ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Holder", "ClrType": "{{typeof(BindingSchema.Holder<TStamp>).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Value", "Type": "Owned", "OwnedType": "Stamp" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    // The class binds: a set serves it, and a write batch takes an object of it.
    public static async Task ExpectBoundAsync<TEntity>(DatabaseImplementationEnum implementation, LatticeSchema schema, TEntity entity)
        where TEntity : class
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, schema);
        var batch = database.CreateWriteBatch();

        Assert.True(schema.TryGetEntityType(typeof(TEntity), out var entityType));
        Assert.Equal(entityType.Name, database.Set<TEntity>().EntityType.Name);

        batch.Insert(entity);
        Assert.Equal(1, batch.Count);
    }

    // The class does not bind, from a set and from every typed member of a write batch, each time
    // with BindingFailed and the given name in the message.
    public static async Task ExpectNotBoundAsync<TEntity>(DatabaseImplementationEnum implementation, LatticeSchema schema, TEntity entity, string named = "Text")
        where TEntity : class
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, schema);
        var batch = database.CreateWriteBatch();

        ExpectBindingFailed(() => database.Set<TEntity>());
        ExpectBindingFailed(() => batch.Insert(entity));
        ExpectBindingFailed(() => batch.Replace(entity));
        ExpectBindingFailed(() => batch.Upsert(entity));
        ExpectBindingFailed(() => batch.Delete<TEntity>(1L));
        Assert.Equal(0, batch.Count);

        void ExpectBindingFailed(Action bind)
        {
            var exception = Assert.Throws<LatticeException>(bind);
            Assert.Equal(LatticeErrorEnum.BindingFailed, exception.Error);
            Assert.Contains(named, exception.Message, StringComparison.Ordinal);
        }
    }
}
