using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Roots.Edits;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Engine.Updates;

// The production edit check uses the entry layout and the result of each effective command.
// This checks the physical limits without allocating millions of entries just to pass their counts.
public sealed class UpdateCollectionBoundTests
{
    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Series",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int32" },
                { "Id": 2, "Name": "Numbers", "Type": "List", "Element": { "Type": "Int64" } },
                { "Id": 3, "Name": "Values", "Type": "Map", "Key": { "Type": "Int64" }, "Value": { "Type": "Int64" } },
                { "Id": 4, "Name": "Members", "Type": "Set", "Element": { "Type": "Int64" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Theory]
    [InlineData("Numbers", nameof(UpdateCommandKindEnum.ListAppend), 8, 33_521_672)]
    [InlineData("Numbers", nameof(UpdateCommandKindEnum.ListInsert), 8, 33_521_672)]
    [InlineData("Values", nameof(UpdateCommandKindEnum.MapSet), 16, 16_760_836)]
    [InlineData("Members", nameof(UpdateCommandKindEnum.SetAdd), 8, 23_465_170)]
    public void A_growing_edit_accepts_the_real_bound_of_its_packed_collection(string name, string operation, int stride, int limit)
    {
        var type = Assert.Single(Schema.EntityTypes);
        var field = type.GetField(name);
        var entry = RootLayout.Compile(type, Schema).Structure.ElementOf(field.Ordinal);
        Assert.NotNull(entry.Packed);
        Assert.Equal(stride, entry.Size);

        var chunks = ValueBlocks.MostChunks;
        var payload = ValueBlocks.ChunkPayload;
        var entries = chunks * payload / stride;
        if (field.Declaration.Type != LatticeFieldTypeEnum.List)
        {
            var lookup = chunks * (payload / sizeof(ulong)) * 7 / 10;
            entries = Math.Min(entries, lookup);
        }

        Assert.Equal(limit, entries);
        Assert.Equal(limit, entry.MostElements);
        var kind = Enum.Parse<UpdateCommandKindEnum>(operation);
        var command = new UpdateCommand(kind, name, [field], 0, 1L, kind == UpdateCommandKindEnum.MapSet ? 2L : null);

        EditedCollection.RequireResultCount(entry, type, command, limit - 1);
        EditedCollection.RequireResultCount(entry, type, command, limit);
        var refusal = Assert.Throws<LatticeException>(() => EditedCollection.RequireResultCount(entry, type, command, limit + 1));
        Assert.Equal(LatticeErrorEnum.ValidationFailed, refusal.Error);
        Assert.Contains($"Series.{name} would hold {limit + 1} entries", refusal.Message);
    }
}
