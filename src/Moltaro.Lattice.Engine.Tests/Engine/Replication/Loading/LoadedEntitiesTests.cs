using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Loading;

// What a child of the engine loads equals what its master holds (brief of Epic 1, sprint S10,
// Acceptance and the planner's block of 17 November 2026, point 5):
// - for every scalar type, in a UTF-8 and in a UTF-16 database: every value of every field of every
//   entity a child reads by name is the value the master reads, as it is stored (EveryTypeValues), with
//   the extremes and the special values of each type and null in the nullable fields;
// - a master and a child opened with different seeds of the hashes of their primary indexes hold the
//   same entities: nothing hashed travels, and the child builds its index with its own seed from the
//   keys it receives. Every key is found on the child, a String key compared without case through its
//   other spelling as well, an absent key is absent, and an enumeration returns every key once.
// The schema, the sets and the positions of a loaded child are stated by the scenario tests
// (Moltaro.Lattice.Tests, Database/ChildLoadTests).
public sealed class LoadedEntitiesTests
{
    private const int Keys = 300;

    private const ulong MasterSeed = 0x9E3779B97F4A7C15;

    private const ulong ChildSeed = 0xD1B54A32D192ED03;

    [Theory]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public async Task Child_reads_every_value_of_every_scalar_type_as_its_master_holds_it(string encoding)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(EveryType.Compile(encoding));

        foreach (var entityType in new[] { "Item", "Twin" })
        {
            var records = master.Set(entityType);

            for (var number = 1; number <= EveryTypeValues.Entities; number++)
            {
                await records.InsertAsync(EveryTypeValues.RecordOf(records.EntityType, number));
            }
        }

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);

        foreach (var entityType in new[] { "Item", "Twin" })
        {
            var onMaster = master.Set(entityType).Enumerate().OrderBy(record => record.Get<long>("Id")).ToList();
            var onChild = child.Set(entityType).Enumerate().OrderBy(record => record.Get<long>("Id")).ToList();

            Assert.Equal(onMaster.Select(record => record["Id"]), onChild.Select(record => record["Id"]));

            for (var index = 0; index < onMaster.Count; index++)
            {
                foreach (var field in onMaster[index].EntityType.Fields)
                {
                    Assert.True(
                        EveryTypeValues.Same(onMaster[index][field.Name], onChild[index][field.Name]),
                        $"{entityType} {onMaster[index]["Id"]}, field {field.Name}: the child read {onChild[index][field.Name] ?? "null"}, the master {onMaster[index][field.Name] ?? "null"}.");
                }
            }
        }
    }

    [Fact]
    public async Task Master_and_child_opened_with_different_index_seeds_hold_the_same_entities()
    {
        var schema = KeySchema.Of("Utf8", "OrdinalIgnoreCase", "String", "Int64", "Guid");
        await using var master = await ReplicationNodes.OpenMasterAsync(schema, new EngineTestInputs { IndexSeed = MasterSeed });

        foreach (var keyType in new[] { "String", "Int64", "Guid" })
        {
            var records = master.Set(keyType);

            foreach (var key in KeysOf(keyType))
            {
                await records.InsertAsync(KeySchema.RecordOf(records, key, $"body of {key}"));
            }
        }

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { IndexSeed = ChildSeed });

        foreach (var keyType in new[] { "String", "Int64", "Guid" })
        {
            var records = child.Set(keyType);
            var keys = KeysOf(keyType);

            foreach (var key in keys)
            {
                Assert.Equal($"body of {key}", records.Get(key)?["Body"]);
            }

            var enumerated = records.Enumerate().Select(record => record["Id"]!).ToList();

            Assert.Equal(keys.Length, enumerated.Count);
            Assert.True(enumerated.ToHashSet().SetEquals(keys), $"An enumeration of {keyType} on the child did not return every key once.");
        }

        Assert.Equal("body of key 7", child.Set("String").Get("KEY 7")?["Body"]);
        Assert.Null(child.Set("String").Get("no such key"));
        Assert.Null(child.Set("Int64").Get(-1L));
        Assert.Null(child.Set("Guid").Get(Guid.Empty));
    }

    private static object[] KeysOf(string keyType) => keyType switch
    {
        "String" => [.. Enumerable.Range(0, Keys).Select(key => (object)$"key {key}")],
        "Int64" => [.. Enumerable.Range(0, Keys).Select(key => (object)((long)key * 7_919))],
        _ => [.. Enumerable.Range(0, Keys).Select(key => (object)new Guid(key + 1, 7, 7, 1, 2, 3, 4, 5, 6, 7, 8))],
    };
}
