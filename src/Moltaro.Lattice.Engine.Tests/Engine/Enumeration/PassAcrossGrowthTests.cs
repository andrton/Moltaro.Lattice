using System.Buffers.Binary;
using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.TestSupport.Text;

namespace Moltaro.Lattice.Engine.Tests.Engine.Enumeration;

// A pass that meets a growth of the primary index returns every entity it has not returned and none it
// has, whatever the key type (the remarks of ILatticeSet<TEntity>.Enumerate; brief of Epic 1, sprint
// S08, Acceptance; review round 1 of S08, part 2). A grown table places every entry anew, so a pass
// cannot go on from its position in the old table: it walks the grown one from its start and skips the
// keys it remembers. Two things decide that, and each test makes one of them decide alone:
// - Where the pass stands. 4,000 entities, 3,600 of them returned, then a growth: a pass that went on
//   from its old position in the grown table would miss the entries the growth moved before that
//   position, those that sat in overflow buckets of the old table among them. The pass must return
//   each of the 4,000 once.
// - What it remembers. Half of the entities returned, and with them every key of the type that is an
//   edge of how a key is held: 0, the smallest and the largest value of an integer type, the empty
//   Guid and Guids with one half 0, among Guids that share one half and differ in the other, and the
//   empty text. Each returned entity is deleted and created again, a String key compared without
//   case under another spelling, and then the index grows, so that the pass meets every key it has
//   returned once more, in the grown table: it must return each identity once, under the equality of
//   its key, and every entity of the start, for a String key without case in a UTF-8 and in a UTF-16
//   database, for Guid keys, and for Int16, Int64 and UInt64 keys.
// The index grows by the count of its entries, and a test inserts new entities until it has grown
// once, which the switch of the engine tells (EngineDatabase.ArmGrowthSwitch). The seed of the index is
// fixed (EngineMaster with a seed), so where an entry lies, and with it what a wrong pass would lose or
// return twice, is the same at every run.
public sealed class PassAcrossGrowthTests
{
    private const ulong Seed = 0x9E3779B97F4A7C15;

    private const int Many = 4_000;

    private const int TakenBeforeTheGrowth = 3_600;

    private const int Edges = 400;

    public static TheoryData<string> KeyTypes => ["Int64", "String", "Guid"];

    public static TheoryData<string> EdgeCases =>
    [
        "String without case, UTF-8",
        "String without case, UTF-16",
        "Guid",
        "Int16",
        "Int64",
        "UInt64",
    ];

    [Theory]
    [MemberData(nameof(KeyTypes))]
    public async Task Pass_that_meets_a_growth_late_returns_every_entity_once(string keyType)
    {
        await using var database = await EngineMaster.OpenAsync(KeySchema.Of("Utf8", "Ordinal", keyType), Seed);
        var records = database.Set(keyType);
        var keys = ManyOf(keyType, 0);

        await InsertAsync(records, keys, "first");

        using var steps = records.Enumerate().GetEnumerator();
        var returned = new List<object>();

        while (returned.Count < TakenBeforeTheGrowth)
        {
            Assert.True(steps.MoveNext());
            returned.Add(steps.Current["Id"]!);
        }

        var fresh = await GrowAsync(database, records, ManyOf(keyType, Many));

        while (steps.MoveNext())
        {
            returned.Add(steps.Current["Id"]!);
        }

        Assert.Equal(returned.Count, returned.Distinct().Count());
        Assert.True(returned.Where(key => !fresh.Contains(key)).ToHashSet().SetEquals(keys), $"The pass did not return each of the {Many} entities of the start once: {keys.Except(returned).Count()} are missing.");
    }

    [Theory]
    [MemberData(nameof(EdgeCases))]
    public async Task Pass_that_meets_its_keys_created_again_after_a_growth_returns_each_identity_once(string edgeCase)
    {
        var (keyType, encoding, comparison) = edgeCase switch
        {
            "String without case, UTF-8" => ("String", "Utf8", "OrdinalIgnoreCase"),
            "String without case, UTF-16" => ("String", "Utf16", "OrdinalIgnoreCase"),
            _ => (edgeCase, "Utf8", "Ordinal"),
        };

        await using var database = await EngineMaster.OpenAsync(KeySchema.Of(encoding, comparison, keyType), Seed);
        var records = database.Set(keyType);
        var (keys, edges) = EdgesOf(keyType);

        await InsertAsync(records, keys, "first");

        // Half of the entities and every edge are returned.
        using var steps = records.Enumerate().GetEnumerator();
        var returned = new List<object>();

        while (returned.Count < keys.Length / 2 || !edges.All(edge => returned.Any(key => IdentityOf(key).Equals(IdentityOf(edge)))))
        {
            Assert.True(steps.MoveNext());
            returned.Add(steps.Current["Id"]!);
        }

        Assert.True(returned.Count < keys.Length, "The pass returned every entity before the change; choose another seed.");

        // Each of them is deleted and created again, under another spelling where the comparison holds
        // it equal, and then the index grows.
        foreach (var key in returned.ToList())
        {
            Assert.True((await records.DeleteAsync(key)).Changed);
            await records.InsertAsync(KeySchema.RecordOf(records, IdentityOf(key), "again"));
        }

        var fresh = await GrowAsync(database, records, FreshOf(keyType));

        while (steps.MoveNext())
        {
            returned.Add(steps.Current["Id"]!);
        }

        var identities = returned.Select(IdentityOf).ToList();
        var ofTheStart = keys.Select(IdentityOf).ToHashSet();
        var ofTheFresh = fresh.Select(IdentityOf).ToHashSet();

        Assert.Equal(identities.Count, identities.Distinct().Count());
        Assert.True(identities.Where(identity => !ofTheFresh.Contains(identity)).ToHashSet().SetEquals(ofTheStart), $"The pass did not return each of the {keys.Length} entities of the start once.");
    }

    // Inserts the entities of the keys, in their order, with the body given.
    private static async Task InsertAsync(ILatticeSet records, object[] keys, string body)
    {
        foreach (var key in keys)
        {
            await records.InsertAsync(KeySchema.RecordOf(records, key, body));
        }
    }

    // Inserts entities of the keys of the supply, in its order, until the primary index of the entity
    // type has grown once, which the switch of the engine tells: it runs inside the insert that grows
    // the index. The keys it inserted.
    private static async Task<HashSet<object>> GrowAsync(EngineDatabase database, ILatticeSet records, IEnumerable<object> supply)
    {
        var grown = false;
        var inserted = new HashSet<object>();

        database.ArmGrowthSwitch(records.EntityType, () => Volatile.Write(ref grown, true));

        foreach (var key in supply)
        {
            await records.InsertAsync(KeySchema.RecordOf(records, key, "fresh"));
            inserted.Add(key);

            if (Volatile.Read(ref grown))
            {
                return inserted;
            }
        }

        Assert.Fail("The primary index did not grow.");

        return inserted;
    }

    // Many keys of the key type, distinct for distinct numbers from the start given: Int64 keys spread
    // over the integers, String keys, and Guids whose two halves both differ.
    private static object[] ManyOf(string keyType, int start) => keyType switch
    {
        "Int64" => [.. Enumerable.Range(start, Many).Select(number => (object)(((long)number * 7_919) + 1))],
        "String" => [.. Enumerable.Range(start, Many).Select(number => (object)string.Create(CultureInfo.InvariantCulture, $"name {number:D5}"))],
        _ => [.. Enumerable.Range(start, Many).Select(number => (object)GuidOf(((long)number * 0x9E37_79B9) + 1, ((long)number * 0x85EB_CA6B) + 2))],
    };

    // The keys of a test of the edges, and its edges among them.
    private static (object[] Keys, object[] Edges) EdgesOf(string keyType)
    {
        object[] edges = keyType switch
        {
            "String" => ["", "a", "ß"],
            "Guid" => [Guid.Empty, GuidOf(0, 1), GuidOf(1, 0), GuidOf(0, -1), GuidOf(-1, 0)],
            "Int16" => [(short)0, short.MinValue, short.MaxValue, (short)-1, (short)1],
            "Int64" => [0L, long.MinValue, long.MaxValue, -1L, 1L],
            _ => [0UL, ulong.MaxValue, (ulong)long.MaxValue, (ulong)long.MaxValue + 1, 1UL],
        };

        return ([.. edges.Concat(OthersOf(keyType, 0)).Distinct().Take(Edges)], edges);
    }

    // Keys of the type beside the edges, from the number given on: String keys with letters that have
    // another case, of characters of one to four bytes in UTF-8, every fiftieth longer than a chunk of
    // the remembered text; Guids of halves from a set of twenty, so that every Guid shares its first
    // half with nineteen others and its second half with nineteen others; integers spread over their
    // type.
    private static IEnumerable<object> OthersOf(string keyType, int start)
    {
        long[] halves =
        [
            0, 1, -1, 2, long.MinValue, long.MaxValue, 0x0123456789ABCDEF, unchecked((long)0xFEDCBA9876543210),
            0x1_0000_0000, unchecked((long)0xFFFF_FFFF_0000_0000), 0xFFFF_FFFF, 0x5555_5555_5555_5555,
            unchecked((long)0xAAAA_AAAA_AAAA_AAAA), 7, 1L << 62, 1L << 31, 3, 0x0F0F_0F0F_0F0F_0F0F, 42, -42,
        ];

        for (var number = start; ; number++)
        {
            yield return keyType switch
            {
                "String" => string.Create(CultureInfo.InvariantCulture, $"key {number:D4} straße é𐐨").PadRight(number % 50 == 0 ? 300 : 0, 'x'),
                "Guid" => GuidOf(halves[(number / halves.Length) % halves.Length] + (number / (halves.Length * halves.Length)), halves[number % halves.Length]),
                "Int16" => (short)((number * 163 % 65_536) - 32_768),
                "Int64" => unchecked(((long)number * (long)0x9E37_79B9_7F4A_7C15) + 5),
                _ => ((ulong)number * 0xD1B5_4A32_D192_ED03) + 5,
            };
        }
    }

    // As many keys as a growth may need, none of them a key of the start.
    private static IEnumerable<object> FreshOf(string keyType)
    {
        var (keys, _) = EdgesOf(keyType);
        var taken = keys.Select(IdentityOf).ToHashSet();

        return OthersOf(keyType, 100_000).Where(key => !taken.Contains(IdentityOf(key))).Distinct().Take(4 * Edges);
    }

    // A key under the equality of its field: the upper case of a String key, which every test of the
    // edges compares without case, and the key itself for the others. The upper case is also the other
    // spelling under which a test creates a String key again.
    private static object IdentityOf(object key) => key is string text ? CaseRule.ToUpper(text) : key;

    // The Guid whose two 64-bit halves, as the engine holds a Guid, are the ones given.
    private static Guid GuidOf(long first, long second)
    {
        Span<byte> bytes = stackalloc byte[16];

        BinaryPrimitives.WriteInt64LittleEndian(bytes, first);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], second);

        return new Guid(bytes);
    }
}
