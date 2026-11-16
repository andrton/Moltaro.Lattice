namespace Moltaro.Lattice.Tests.Keys;

// Keys whose hash codes are all the same are keys like any other (review round 1 of Epic 1, sprint
// S08, part 1: the primary index of the engine wrote past its table when many keys shared a hash).
// Int64.GetHashCode is the exclusive or of the two halves of the value, so every key (i << 32) | i has
// the hash code 0. What that means differs by implementation, and the test states what each has to
// do: the placeholder keeps its entities in a dictionary that hashes a key by its hash code, so there
// all 300 keys meet in one hash; the engine mixes every bit of an Int64 key into its hash, so there
// keys of this structure spread as any others do, and the chains of entries of one hash are tested on
// its index directly (OneHashChainTests of Moltaro.Lattice.Engine.Tests). On both: every one of 300
// such keys is inserted and found, through the class and by name; 50 other keys of hash code 0 that
// were never inserted are absent; an enumeration returns each key once; each is deleted, half through
// the class and half by name, after which none is found and an enumeration is empty; and all of them
// can be inserted again.
//
// String keys of one hash cannot be made cheaply: the hash of a String key compared with Ordinal is
// string.GetHashCode, which is seeded at random in every process, so 300 texts of one hash would take
// about 2^32 tries each.
public sealed class ZeroHashCodeKeyTests
{
    private const int Count = 300;

    private const int Absent = 50;

    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Clash", "ClrType": "{{typeof(Clash).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Label", "Type": "String" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Entities_whose_keys_all_have_the_hash_code_zero_are_inserted_found_enumerated_once_and_deleted(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, Schema);
        var clashes = database.Set<Clash>();
        var records = database.Set("Clash");
        long[] keys = [.. Enumerable.Range(1, Count).Select(KeyOf)];

        // The premise: the hash code 0 for every key, also for the absent ones.
        Assert.All(keys, key => Assert.Equal(0, key.GetHashCode()));
        Assert.Equal(0, KeyOf(Count + Absent).GetHashCode());

        foreach (var key in keys)
        {
            await clashes.InsertAsync(new Clash { Id = key, Label = LabelOf(key) });
        }

        foreach (var key in keys)
        {
            Assert.Equal(LabelOf(key), clashes.Get(key)?.Label);
            Assert.Equal(LabelOf(key), records.Get(key)?["Label"]);
        }

        for (var absent = Count + 1; absent <= Count + Absent; absent++)
        {
            Assert.Null(clashes.Get(KeyOf(absent)));
            Assert.Null(records.Get(KeyOf(absent)));
        }

        Assert.Equal(keys, clashes.Enumerate().Select(clash => clash.Id).Order());

        for (var index = 0; index < keys.Length; index++)
        {
            var deleted = index % 2 == 0 ? await clashes.DeleteAsync(keys[index]) : await records.DeleteAsync(keys[index]);

            Assert.True(deleted.Changed);
        }

        Assert.All(keys, key => Assert.Null(clashes.Get(key)));
        Assert.Empty(clashes.Enumerate());

        foreach (var key in keys)
        {
            await clashes.InsertAsync(new Clash { Id = key, Label = LabelOf(key) });
        }

        Assert.Equal(keys, clashes.Enumerate().Select(clash => clash.Id).Order());
    }

    private static long KeyOf(int number) => ((long)number << 32) | (uint)number;

    private static string LabelOf(long key) => $"clash {key}";

    private sealed class Clash
    {
        public long Id { get; set; }

        public string Label { get; set; } = "";
    }
}
