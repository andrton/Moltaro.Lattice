namespace Moltaro.Lattice.Tests.Binding;

// An object of a bound class is made by its public parameterless constructor, so an abstract
// class does not bind, as the class of an entity type or of an owned type. It is refused with
// BindingFailed like every other class that does not agree with the schema, and never with
// another exception.
public sealed class AbstractClassBindingTests
{
    [Fact]
    public async Task Abstract_class_of_an_entity_type_is_refused_with_BindingFailed()
    {
        await using var database = await TestData.OpenMasterAsync(schema: LatticeSchema.Compile($$"""
            {
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Note", "ClrType": "{{typeof(AbstractNote).FullName}}",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """));

        TestData.ExpectError(LatticeErrorEnum.BindingFailed, () => database.Set<AbstractNote>());
        TestData.ExpectError(LatticeErrorEnum.BindingFailed, () => database.CreateWriteBatch().Delete<AbstractNote>(1L));
    }

    [Fact]
    public async Task Abstract_class_of_an_owned_type_is_refused_with_BindingFailed()
    {
        await using var database = await TestData.OpenMasterAsync(schema: LatticeSchema.Compile($$"""
            {
              "OwnedTypes": [
                { "Id": 1, "Name": "Badge", "ClrType": "{{typeof(AbstractBadge).FullName}}", "Fields": [ { "Id": 1, "Name": "Text", "Type": "String" } ] }
              ],
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Holder", "ClrType": "{{typeof(BindingSchema.Holder<AbstractBadge>).FullName}}",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Value", "Type": "Owned", "OwnedType": "Badge" } ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """));

        var exception = Assert.Throws<LatticeException>(() => database.Set<BindingSchema.Holder<AbstractBadge>>());
        Assert.Equal(LatticeErrorEnum.BindingFailed, exception.Error);
        Assert.Contains(nameof(AbstractBadge), exception.Message);
    }

    // Each has a public parameterless constructor, which still makes no object of it.
    private abstract class AbstractNote
    {
        public AbstractNote()
        {
        }

        public long Id { get; set; }
    }

    private abstract class AbstractBadge
    {
        public AbstractBadge()
        {
        }

        public string Text { get; set; } = "";
    }
}
