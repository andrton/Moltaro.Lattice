using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// ReadMemoryLimit counts an entity's whole stored tree, including a Map/Set's derived lookup
// (arena-protocol.md, "The reads of an entity table"; queries.md, Q07.1.1).
public sealed class CollectionEntityBytesTests
{
    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Value", "Type": "Int32" },
                { "Id": 3, "Name": "Pairs", "Type": "Map", "Key": { "Type": "Int64" }, "Value": { "Type": "Int64" } },
                { "Id": 4, "Name": "Tags", "Type": "Set", "Element": { "Type": "String", "StringStorage": "Dictionary" } }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByValue", "Fields": [2] } ]
            }
          ]
        }
        """);

    [Fact]
    public async Task The_bytes_of_compact_hashed_and_chunked_collections_agree_with_owned_blocks_after_churn()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var items = database.Set("Item");
        foreach (var count in new[] { 8, 9, 5_730, 5_731, 1_000, 4, 5_731, 0 })
        {
            await items.UpsertAsync(Item(items, 1, count));
            await items.UpsertAsync(Item(items, 2, Math.Min(count, 8)));
            OwnershipAudit.Expect(database);

            database.ForEachLiveRoot((arena, layout, handle, root) =>
            {
                var visitor = new AuditVisitor(handle, [], []);
                StructureWalk.Walk(arena, layout, root, ref visitor);
                var expected = BlockHeader.Size + arena.Header(root).PayloadLength + visitor.StoredBytes;

                Assert.Equal(expected, new RootReader(arena, layout, root).EntityBytes());
            });
        }

        await items.DeleteAsync(1L);
        await items.DeleteAsync(2L);
        OwnershipAudit.Expect(database);
    }

    [Fact]
    public async Task A_search_counts_the_derived_lookup_and_hits_its_exact_memory_boundary()
    {
        // A fixed part of 76 bytes, 9 packed pairs of 16 bytes, and a 128-byte lookup payload.
        // The Set is empty. Two matches make the read retain its initial key table of 136 bytes
        // and answer array of four references (32 bytes), as AnswerMemoryTests specifies.
        const int EntityBytes = (32 + 8 + 4 + 2 * 16) + (9 * 16) + 128;
        const int Limit = 2 * EntityBytes + 136 + 32;

        Assert.Equal(2, (await FindAsync(Limit)).Count);
        var error = await Assert.ThrowsAsync<LatticeException>(() => FindAsync(Limit - 1));

        Assert.Equal(LatticeErrorEnum.ReadLimitExceeded, error.Error);
        Assert.Equal(LatticeLimitEnum.ReadMemory, error.Limit);
    }

    private static async Task<IReadOnlyList<LatticeRecord>> FindAsync(long limit)
    {
        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            Schema = Schema,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            ReadMemoryLimit = limit,
        });
        var items = database.Set("Item");
        for (var id = 1L; id <= 2; id++)
        {
            var item = Item(items, id, 9);
            item["Tags"] = Array.Empty<string>();
            await items.InsertAsync(item);
        }

        return (await items.Index("ByValue").FindAsync(IndexKey.Equal(1), new LatticeFindOptions())).Items;
    }

    private static LatticeRecord Item(ILatticeSet items, long id, int count) => new(items.EntityType)
    {
        ["Id"] = id,
        ["Value"] = 1,
        ["Pairs"] = Enumerable.Range(0, count).ToDictionary(key => (long)key, key => (long)key),
        ["Tags"] = Enumerable.Range(0, count).Select(key => $"tag-{key}"),
    };
}
