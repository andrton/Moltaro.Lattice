using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// The schema of the tests of the primary index, and the class of its one entity type Clash: the key
// Id, an Int64, and Number, an Int64, both in the fixed part, so that an entity is small and a test of
// the index can insert thousands of them. One entity type, so that PrimaryIndexBytes is the bytes of
// its index alone.
//
// The keys a test chooses decide the hashes: the engine hashes an Int64 key by Int64.GetHashCode, the
// exclusive or of the two halves of the value. OneHash(i) = (i << 32) | i has the hash 0 for every i;
// Composite(shard, id) = (shard << 32) | id has the hash shard ^ id, which as many keys share as there
// are shards; a small key i has the hash i.
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

    public static long OneHash(int number) => ((long)number << 32) | (uint)number;

    public static long Composite(int shard, int id) => ((long)shard << 32) | (uint)id;

    public static Clash Of(long key) => new() { Id = key, Number = key };

    public sealed class Clash
    {
        public long Id { get; set; }

        public long Number { get; set; }
    }
}
