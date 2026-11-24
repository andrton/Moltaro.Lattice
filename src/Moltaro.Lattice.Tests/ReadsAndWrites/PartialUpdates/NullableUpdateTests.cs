namespace Moltaro.Lattice.Tests.ReadsAndWrites.PartialUpdates;

public sealed class NullableUpdateTests
{
    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item", "ClrType": "{{typeof(NullableUpdateItem).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "String" },
                { "Id": 2, "Name": "Rank", "Type": "Int32", "Nullable": true },
                { "Id": 3, "Name": "Scores", "Type": "List", "Nullable": true, "Element": { "Type": "Int32" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Compatible_typed_null_clears_a_scalar_and_a_whole_collection(bool byClass)
    {
        await using var database = await TestData.OpenMasterAsync(schema: Schema);
        var items = database.Set<NullableUpdateItem>();
        var records = database.Set("Item");
        await items.InsertAsync(new NullableUpdateItem { Id = "ada", Rank = 42, Scores = [10, 20] });
        Assert.True(items.TryGet("ada", out var previous, out var revision));
        Assert.Equal(42, previous.Rank);
        Assert.Equal(new[] { 10, 20 }, previous.Scores);

        var update = byClass ? items.CreateUpdate("ada") : records.CreateUpdate("ada");
        Assert.Throws<ArgumentException>(() => update.Assign<decimal?>("Rank", null));
        Assert.Throws<ArgumentException>(() => update.Assign<List<short>?>("Scores", null));
        Assert.Equal(0, update.Count);

        var rank = update.Assign<int?>("Rank", null);
        var scores = update.Assign<List<int>?>("Scores", null);
        var result = await update.CommitAsync(revision);

        Assert.True(result.Changed);
        Assert.True(rank.Changed);
        Assert.True(scores.Changed);
        Assert.Equal(result.Position, database.Position);
        Assert.True(items.TryGet("ada", out var stored, out var after));
        Assert.NotEqual(revision, after);
        Assert.Null(stored.Rank);
        Assert.Null(stored.Scores);
        var record = records.Get("ada")!;
        Assert.Null(record["Rank"]);
        Assert.Null(record["Scores"]);
    }
}
