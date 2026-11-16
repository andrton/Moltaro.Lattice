using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.TestSupport.Text;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// Each database instance draws a seed when it opens, and every hash of its primary index is computed
// with it, for every key type and both comparisons of a String key; a String key compared with Ordinal
// no longer uses the hash of .NET (the planner's answer to Q50; brief of Epic 1, sprint S08, "Carried
// by part 2 under O16"). The seed is never stored, sent or shown, so a test fixes it through the
// internal entry of the engine that takes one (EngineDatabase.Create with a seed, exception X3):
// - 2,000 Int64 keys built to share one hash under the seed 0 (CollidingKeys) are spread under another
//   seed: they take of the primary index no more than a quarter beyond what 2,000 sequential keys take,
//   as CollidingHashTests measures keys that spread, while under the seed 0 their one chain takes more
//   than that, one overflow bucket of 64 bytes for every seven entries. The bytes are read after a
//   drain, and no time is measured.
// - Under two seeds the test fixes and under one the instance draws, in a UTF-8 and in a UTF-16
//   database, with a String key compared with Ordinal and without case, and with an Int64 and a Guid
//   key: every key is found, through the spelling of the comparison as well, absent keys are absent,
//   deleted keys are absent and the others found, and an enumeration returns each key once.
// - Two instances with the same data and different seeds return the same entities from an
//   enumeration, compared as sets: the order of an enumeration is unspecified and may differ between
//   them, so no test asserts one.
public sealed class IndexSeedTests
{
    private const int Colliding = 2_000;

    private const double MostOverSequential = 1.25;

    private const int Count = 300;

    // The seed under which the colliding keys share one hash, and two others.
    private const ulong Unmixed = 0;

    private const ulong SeedB = 0x9E3779B97F4A7C15;

    private const ulong SeedC = 0xD1B54A32D192ED03;

    public static IEnumerable<TheoryDataRow<string, string, string>> Cases =>
        from encoding in new[] { "Utf8", "Utf16" }
        from comparison in new[] { "Ordinal", "OrdinalIgnoreCase" }
        from seed in new[] { "B", "C", "drawn" }
        select new TheoryDataRow<string, string, string>(encoding, comparison, seed);

    [Fact]
    public async Task Keys_built_to_share_one_hash_under_one_seed_are_spread_under_another()
    {
        var keys = CollidingKeys.Of(Colliding);

        // The premise, apart from the engine: under the seed 0 every key has the one hash.
        Assert.Equal(Colliding, keys.Distinct().Count());
        Assert.All(keys, key => Assert.Equal(CollidingKeys.Hash, (uint)CollidingKeys.Mix((ulong)key)));

        var sequential = await IndexBytesOfAsync(Enumerable.Range(0, Colliding).Select(key => (long)key), SeedB);
        var underTheirSeed = await IndexBytesOfAsync(keys, Unmixed);
        var underAnother = await IndexBytesOfAsync(keys, SeedB);

        Assert.True(
            underTheirSeed > MostOverSequential * sequential,
            $"Under the seed 0 the primary index takes {underTheirSeed} bytes for {Colliding} keys built to share one hash, {underTheirSeed / (double)sequential:F2} times the {sequential} of as many sequential keys: they did not share a chain.");
        Assert.True(
            underAnother <= MostOverSequential * sequential,
            $"Under another seed the primary index takes {underAnother} bytes for {Colliding} keys built to share one hash under the seed 0, {underAnother / (double)sequential:F2} times the {sequential} of as many sequential keys, more than {MostOverSequential}: the seed did not spread them.");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Every_key_is_found_and_enumerated_once_whatever_the_seed(string encoding, string comparison, string seed)
    {
        await using var database = await OpenAsync(SchemaOf(encoding, comparison), seed);
        var ignoresCase = comparison == "OrdinalIgnoreCase";

        await ExpectEveryKeyAsync(database.Set("Note"), Enumerable.Range(0, Count).Select(TextOf).ToArray(), TextOf(Count + 1), ignoresCase);
        await ExpectEveryKeyAsync(database.Set("Count"), Enumerable.Range(0, Count).Select(key => (object)((long)key * 7_919)).ToArray(), -1L, ignoresCase: false);
        await ExpectEveryKeyAsync(database.Set("Tick"), Enumerable.Range(0, Count).Select(key => (object)GuidOf(key)).ToArray(), GuidOf(Count + 1), ignoresCase: false);
    }

    [Fact]
    public async Task Two_instances_with_the_same_data_and_different_seeds_return_the_same_entities_from_an_enumeration()
    {
        var schema = SchemaOf("Utf8", "Ordinal");

        await using var first = await OpenAsync(schema, "B");
        await using var second = await OpenAsync(schema, "C");

        foreach (var database in new[] { first, second })
        {
            for (var key = 0; key < Count; key++)
            {
                await database.Set("Note").InsertAsync(RecordOf(database.Set("Note"), TextOf(key)));
                await database.Set("Count").InsertAsync(RecordOf(database.Set("Count"), (long)key));
            }
        }

        foreach (var entityType in new[] { "Note", "Count" })
        {
            foreach (var withSession in new[] { false, true })
            {
                var ofFirst = await KeysOfAsync(first, entityType, withSession);
                var ofSecond = await KeysOfAsync(second, entityType, withSession);

                Assert.Equal(Count, ofFirst.Count);
                Assert.True(ofFirst.SetEquals(ofSecond), $"The two instances returned other {entityType} entities.");
            }
        }
    }

    // A key of every key type is inserted, found, through the spelling of the comparison as well,
    // enumerated once, deleted where its number is a multiple of five, and then absent while the others
    // are found. The absent key is absent throughout.
    private static async Task ExpectEveryKeyAsync(ILatticeSet records, object[] keys, object absent, bool ignoresCase)
    {
        foreach (var key in keys)
        {
            await records.InsertAsync(RecordOf(records, key));
        }

        foreach (var key in keys)
        {
            Assert.Equal(key, records.Get(key)?[records.EntityType.Key.Name]);

            if (key is string text && CaseRule.ToUpper(text) is var upper && upper != text)
            {
                Assert.Equal(ignoresCase ? key : null, records.Get(upper)?[records.EntityType.Key.Name]);
            }
        }

        Assert.Null(records.Get(absent));

        var enumerated = records.Enumerate().Select(record => record[records.EntityType.Key.Name]).ToList();

        Assert.Equal(keys.Length, enumerated.Count);
        Assert.True(enumerated.ToHashSet().SetEquals(keys), $"An enumeration of {records.EntityType.Name} did not return every key once.");

        for (var index = 0; index < keys.Length; index += 5)
        {
            Assert.True((await records.DeleteAsync(keys[index])).Changed);
        }

        for (var index = 0; index < keys.Length; index++)
        {
            var found = records.Get(keys[index]);

            Assert.Equal(index % 5 == 0 ? null : keys[index], found?[records.EntityType.Key.Name]);
        }

        Assert.Null(records.Get(absent));
    }

    // The keys an enumeration by name returns, with a session or without, as a set.
    private static async Task<HashSet<object>> KeysOfAsync(EngineDatabase database, string entityType, bool withSession)
    {
        var records = database.Set(entityType);
        var key = records.EntityType.Key.Name;
        var returned = withSession
            ? await records.EnumerateAsync(new LatticeSession()).Select(record => record[key]!).ToListAsync()
            : records.Enumerate().Select(record => record[key]!).ToList();

        Assert.Equal(returned.Count, returned.Distinct().Count());

        return [.. returned];
    }

    // The bytes of the primary index of a database of its own, opened with the seed, that holds these
    // keys, after a drain.
    private static async Task<long> IndexBytesOfAsync(IEnumerable<long> keys, ulong indexSeed)
    {
        await using var database = await ClashSchema.OpenAsync(indexSeed);
        var clashes = database.Set<ClashSchema.Clash>();

        foreach (var key in keys)
        {
            await clashes.InsertAsync(ClashSchema.Of(key));
        }

        database.DrainRetired();

        return database.Counters.PrimaryIndexBytes;
    }

    private static Task<EngineDatabase> OpenAsync(LatticeSchema schema, string seed) => seed switch
    {
        "B" => EngineMaster.OpenAsync(schema, SeedB),
        "C" => EngineMaster.OpenAsync(schema, SeedC),
        _ => EngineMaster.OpenAsync(schema),
    };

    // Note, with a String key compared as given, Count with an Int64 key and Tick with a Guid key, in a
    // schema of the encoding given; none has a class.
    private static LatticeSchema SchemaOf(string encoding, string comparison) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note",
              "Fields": [ { "Id": 1, "Name": "Name", "Type": "String", "Comparison": "{{comparison}}" }, { "Id": 2, "Name": "Body", "Type": "String" } ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Count",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Body", "Type": "String" } ],
              "PrimaryKey": [1]
            },
            {
              "Id": 3, "Name": "Tick",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Guid" }, { "Id": 2, "Name": "Body", "Type": "String" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private static LatticeRecord RecordOf(ILatticeSet records, object key) =>
        new(records.EntityType) { [records.EntityType.Key.Name] = key, ["Body"] = $"body of {key}" };

    // Text keys of one to four bytes a character in UTF-8, a surrogate pair among them, of lengths from
    // a few characters to a hundred, so that a hash of the text meets every form of it.
    private static string TextOf(int key) =>
        string.Create(CultureInfo.InvariantCulture, $"Key {key} aé€𐐨 ").PadRight(5 + (key % 96), 'x');

    private static Guid GuidOf(int key) => new(key, (short)(key >> 16), 7, 1, 2, 3, 4, 5, 6, 7, 8);
}
