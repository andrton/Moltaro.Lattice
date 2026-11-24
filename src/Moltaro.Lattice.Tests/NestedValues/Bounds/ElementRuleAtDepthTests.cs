namespace Moltaro.Lattice.Tests.NestedValues.Bounds;

// The rule of the schema language that the record of an element of a List fits one chunk holds at every depth
// of Lists, as at the first (LatticeEntityType): Deep holds Rows, a List of Outer, whose Inner is a List of
// Cell, an owned value of k Decimals that holds no descriptor, so its record takes 16k bytes, at most the
// 65,504 bytes of one chunk. With k = 4,094, 65,504 bytes, the database opens; with k = 4,095, 65,520 bytes,
// its open fails with InvalidSchema, two Lists deep.
public sealed class ElementRuleAtDepthTests
{
    [Fact]
    public async Task Record_of_an_element_two_Lists_deep_that_fits_one_chunk_opens_and_one_that_does_not_is_InvalidSchema()
    {
        await using (var fits = await TestDatabases.OpenMasterAsync(SchemaOf(4_094)))
        {
            Assert.Equal(LatticeDatabaseStateEnum.Ready, fits.State);
        }

        var database = TestDatabases.Create(new LatticeDatabaseOptions { PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, Schema = SchemaOf(4_095) });
        var exception = await Assert.ThrowsAsync<LatticeException>(async () => await database.OpenAsync());

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains("Deep", exception.Message, StringComparison.Ordinal);
    }

    private static LatticeSchema SchemaOf(int decimals) => LatticeSchema.Compile($$"""
        {
          "OwnedTypes": [
            { "Id": 1, "Name": "Cell", "Fields": [ {{string.Join(", ", Enumerable.Range(1, decimals).Select(id => $$"""{ "Id": {{id}}, "Name": "D{{id}}", "Type": "Decimal" }"""))}} ] },
            { "Id": 2, "Name": "Outer", "Fields": [ { "Id": 1, "Name": "Inner", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Cell" } } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Deep",
              "Fields": [
                { "Id": 1, "Name": "Code", "Type": "String" },
                { "Id": 2, "Name": "Rows", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Outer" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);
}
