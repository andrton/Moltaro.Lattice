using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Primary;

// The schema of the tests of the primary index, and the class of its one entity type Clash: the key
// Id, an Int64, and Number, an Int64, both in the fixed part, so that an entity is small and a test of
// the index can insert thousands of them. One entity type, so that PrimaryIndexBytes is the bytes of
// its index alone.
//
// Keys of a structure that Int64.GetHashCode, the exclusive or of the two halves of the value, folds
// together: ZeroHashCode(i) = (i << 32) | i has the hash code 0 for every i, and Composite(shard, id) =
// (shard << 32) | id the hash code shard ^ id, which as many keys share as there are shards. The engine
// does not hash a key by its hash code: it mixes every bit of an Int64 key into its hash (review round
// 1 of S08, part 1; Entities.EntityTable), so such keys spread over the index as any others do. The
// chains of entries of one hash are tested with hashes given to the index directly
// (OneHashChainTests).
internal static class ClashSchema
{
    public static LatticeSchema Schema { get; } = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Clash", "ClrType": "{{typeof(Clash).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Number", "Type": "Int64" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    public static Task<EngineDatabase> OpenAsync() => EngineMaster.OpenAsync(Schema);

    public static Task<EngineDatabase> OpenAsync(ulong indexSeed) => EngineMaster.OpenAsync(Schema, indexSeed);

    public static long ZeroHashCode(int number) => ((long)number << 32) | (uint)number;

    public static long Composite(int shard, int id) => ((long)shard << 32) | (uint)id;

    public static Clash Of(long key) => new() { Id = key, Number = key };

    public sealed class Clash
    {
        public long Id { get; set; }

        public long Number { get; set; }
    }
}
