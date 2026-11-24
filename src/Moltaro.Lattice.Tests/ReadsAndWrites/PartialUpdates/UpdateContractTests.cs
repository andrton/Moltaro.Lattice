using Moltaro.Lattice.TestSupport.PartialUpdates;

namespace Moltaro.Lattice.Tests.ReadsAndWrites.PartialUpdates;

public sealed class UpdateContractTests
{
    [Fact]
    public void An_entity_type_resolves_a_declared_field_by_its_exact_name()
    {
        var type = Assert.Single(UpdateSchema.Schema.EntityTypes);
        Assert.True(type.TryGetField("Login", out var field));
        Assert.Same(field, type.GetField("Login"));
        Assert.Same(type.Key, type.GetField("Id"));
        Assert.Throws<ArgumentException>("name", () => type.GetField("login"));
        Assert.Throws<ArgumentException>("name", () => type.GetField("Home.City"));
        Assert.Throws<ArgumentException>("name", () => type.GetField(""));
        Assert.Throws<ArgumentNullException>("name", () => type.GetField(null!));
    }

    [Theory]
    [InlineData("Rows", "Ident.ifier", "Value", "Part")]
    [InlineData("Rows", "Id", "Val.ue", "Part")]
    [InlineData("Value", "Id", "Value", "Pa.rt")]
    public void A_dot_in_a_field_name_refuses_the_schema_and_names_the_field(string owner, string keyName, string valueName, string partName)
    {
        var descriptor = $$"""
            {
              "OwnedTypes": [
                { "Id": 1, "Name": "Value", "Fields": [ { "Id": 1, "Name": "{{partName}}", "Type": "Int32" } ] }
              ],
              "EntityTypes": [
                { "Id": 1, "Name": "Rows", "PrimaryKey": [1], "Fields": [
                  { "Id": 1, "Name": "{{keyName}}", "Type": "String" },
                  { "Id": 2, "Name": "{{valueName}}", "Type": "Owned", "OwnedType": "Value" }
                ] }
              ]
            }
            """;
        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile(descriptor));
        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        var dotted = Assert.Single(new[] { keyName, valueName, partName }, field => field.Contains('.'));
        Assert.Contains($"{owner}.{dotted}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_keys_and_null_batch_updates_are_refused_at_the_call()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UpdateSchema.Schema);
        var typed = database.Set<UpdateItem>();
        var records = database.Set("Item");
        var batch = database.CreateWriteBatch();

        Assert.Throws<ArgumentException>(() => typed.CreateUpdate(1));
        Assert.Throws<ArgumentException>(() => records.CreateUpdate(1));
        Assert.Throws<ArgumentException>(() => typed.CreateUpdate("\uD800"));
        Assert.Throws<ArgumentException>(() => records.CreateUpdate("\uD800"));
        Assert.Throws<ArgumentNullException>(() => typed.CreateUpdate<string>(null!));
        Assert.Throws<ArgumentNullException>(() => records.CreateUpdate<string>(null!));
        Assert.Throws<ArgumentNullException>("update", () => batch.Update(null!));
        Assert.Throws<ArgumentNullException>("update", () => batch.Update(null!, default));
        Assert.Equal(0, batch.Count);
    }

    [Fact]
    public void An_unassociated_command_result_has_no_outcome()
    {
        LatticeCommandResult result = default;
        Assert.Throws<InvalidOperationException>(() => result.Changed);
    }
}
