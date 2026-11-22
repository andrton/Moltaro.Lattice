using Moltaro.Lattice.TestSupport.Text;

namespace Moltaro.Lattice.Tests.Indexes.Unique;

// Two keys of a unique index are one value exactly when the comparison of every part holds them equal
// (decision on unique indexes, "Equality follows every component's schema comparator"), at the edges of
// that comparison:
// - a text longer than an entry copies and longer than a piece of a comparison, Plain and Dictionary, in a
//   UTF-8 and a UTF-16 database, one value in every case of it without case and another when one code unit
//   differs, also a letter of a supplementary plane where a piece of the comparison ends, and its owner
//   replaces itself keeping it, in the same case or another;
// - a pair of texts that the rule of equality without case holds equal or different (TestSupport CaseRule,
//   the simple uppercase mapping of Unicode), and the search agrees with the check;
// - composite keys whose parts end at other places, an empty part and a part with U+0000;
// - a Decimal of another scale and its negative zero, a DateTimeOffset of the same instant at another offset;
// - a composite key of a NaN and a text without case.
// Each test stores an entity with a value, refuses an insert and a replace of another entity for each value
// held equal, naming the index, and accepts one for each value held different; afterwards a search for the
// value finds its one owner. Edges, below.
public sealed class PartEdgeTests
{
    // Longer than an entry copies (64 bytes) and than a piece of a comparison (256 code units).
    private const int Long = 300;

    public static IEnumerable<TheoryDataRow<string, string>> Forms =>
        from encoding in new[] { "Utf8", "Utf16" }
        from storage in new[] { "Plain", "Dictionary" }
        select new TheoryDataRow<string, string>(encoding, storage);

    public static IEnumerable<TheoryDataRow<string, string>> CasePairs =>
    [
        new("ı", "I"),
        new("ı", "i"),
        new("ſ", "S"),
        new("ς", "σ"),
        new("ǅ", "ǆ"),
        new("ǅ", "Ǆ"),
        new("\u212A", "k"),
        new("ß", "SS"),
        new("ß", "ẞ"),
        new("ﬀ", "FF"),
        new("\U00010428", "\U00010400"),
        new("İ", "i"),
    ];

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Long_text_is_one_value_in_every_case_of_it_without_case_and_another_when_one_code_unit_differs(string encoding, string storage)
    {
        await using var database = await TestData.OpenMasterAsync(schema: Edges(encoding, storage));
        var held = new string('a', Long - 1) + "z";
        var upperAtThePiece = string.Concat(held.AsSpan(0, 255), "A", held.AsSpan(256));
        var letterAcrossThePiece = new string('a', 255) + "\U00010428" + new string('b', Long - 257);

        await ExpectOneValueAsync(database, first: 100, "Exact", held, taken: [held], free: [new string('a', Long - 1) + "y", "b" + held[1..], held[..^1], held + "a", held.ToUpperInvariant()]);
        await ExpectOneValueAsync(database, first: 200, "Folded", held, taken: [held.ToUpperInvariant(), upperAtThePiece], free: [new string('a', Long - 1) + "y", held[..^1], held + "a"]);
        await ExpectOneValueAsync(database, first: 300, "Folded", letterAcrossThePiece, taken: [letterAcrossThePiece.Replace("\U00010428", "\U00010400", StringComparison.Ordinal), letterAcrossThePiece.ToUpperInvariant()], free: [letterAcrossThePiece.Replace("\U00010428", "\U00010429", StringComparison.Ordinal)]);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Owner_of_a_long_text_value_replaces_itself_keeping_it_in_the_same_or_another_case(string encoding, string storage)
    {
        await using var database = await TestData.OpenMasterAsync(schema: Edges(encoding, storage));
        var edges = database.Set("Edge");
        var held = new string('a', Long - 1) + "z";

        await edges.InsertAsync(RecordOf(edges, 1, ("Exact", held), ("Folded", held)));
        await edges.ReplaceAsync(RecordOf(edges, 1, ("Exact", held), ("Folded", held), ("A", "changed")));
        await edges.ReplaceAsync(RecordOf(edges, 1, ("Exact", held), ("Folded", held.ToUpperInvariant())));

        await UniqueSchemas.ExpectTakenAsync("ByExact", () => edges.InsertAsync(RecordOf(edges, 2, ("Exact", held))));
        await UniqueSchemas.ExpectTakenAsync("ByFolded", () => edges.InsertAsync(RecordOf(edges, 3, ("Folded", held))));
        Assert.Equal([1L], UniqueSchemas.IdsOf(edges.Index("ByExact").Find(IndexKey.Equal(held))));
        Assert.Equal([1L], UniqueSchemas.IdsOf(edges.Index("ByFolded").Find(IndexKey.Equal(held))));
        Assert.Equal(held.ToUpperInvariant(), edges.Get(1L)!["Folded"]);
    }

    [Theory]
    [MemberData(nameof(CasePairs))]
    public async Task Pair_of_texts_is_one_value_without_case_exactly_when_the_rule_holds_them_equal(string held, string other)
    {
        foreach (var encoding in new[] { "Utf8", "Utf16" })
        {
            await using var database = await TestData.OpenMasterAsync(schema: Edges(encoding, "Plain"));

            if (CaseRule.Equal(held, other))
            {
                await ExpectOneValueAsync(database, first: 100, "Folded", held, taken: [other], free: []);
            }
            else
            {
                await ExpectOneValueAsync(database, first: 100, "Folded", held, taken: [], free: [other]);
            }
        }
    }

    [Fact]
    public async Task Composite_text_keys_whose_parts_end_at_other_places_are_other_values()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Edges("Utf8", "Plain"));
        var edges = database.Set("Edge");

        await edges.InsertAsync(RecordOf(edges, 1, ("A", "ab"), ("B", "c")));
        await edges.InsertAsync(RecordOf(edges, 2, ("A", "a\0"), ("B", "b")));
        await edges.InsertAsync(RecordOf(edges, 3, ("A", ""), ("B", "x")));

        await edges.InsertAsync(RecordOf(edges, 11, ("A", "a"), ("B", "bc")));
        await edges.InsertAsync(RecordOf(edges, 12, ("A", "a"), ("B", "\0b")));
        await edges.InsertAsync(RecordOf(edges, 13, ("A", "x"), ("B", "")));
        await edges.InsertAsync(RecordOf(edges, 14, ("A", "a"), ("B", "\0")));

        await UniqueSchemas.ExpectTakenAsync("ByAB", () => edges.InsertAsync(RecordOf(edges, 21, ("A", "ab"), ("B", "c"))));
        await UniqueSchemas.ExpectTakenAsync("ByAB", () => edges.InsertAsync(RecordOf(edges, 22, ("A", "a\0"), ("B", "b"))));
        await UniqueSchemas.ExpectTakenAsync("ByAB", () => edges.ReplaceAsync(RecordOf(edges, 13, ("A", ""), ("B", "x"))));

        Assert.Equal([1L], UniqueSchemas.IdsOf(edges.Index("ByAB").Find(IndexKey.Equal("ab", "c"))));
        Assert.Equal([11L], UniqueSchemas.IdsOf(edges.Index("ByAB").Find(IndexKey.Equal("a", "bc"))));
        Assert.Equal([12L], UniqueSchemas.IdsOf(edges.Index("ByAB").Find(IndexKey.Equal("a", "\0b"))));
        Assert.Equal([14L], UniqueSchemas.IdsOf(edges.Index("ByAB").Find(IndexKey.Equal("a", "\0"))));
        Assert.Equal(7, edges.Enumerate().Count());
    }

    [Fact]
    public async Task Decimal_of_another_scale_and_a_DateTimeOffset_of_the_same_instant_at_another_offset_are_one_value()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Edges("Utf8", "Plain"));
        var instant = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(0, decimal.Compare(-0.0m, 0m));

        await ExpectOneValueAsync(database, first: 100, "Amount", 1.0m, taken: [1m, 1.00m, 1.000000000000m], free: [1.0000000000000000000000000001m, -1m]);
        await ExpectOneValueAsync(database, first: 200, "Amount", 0m, taken: [-0.0m, 0.000m], free: [0.0000000000000000000000000001m]);
        await ExpectOneValueAsync(database, first: 300, "At", instant, taken: [instant.ToOffset(TimeSpan.FromHours(5)), instant.ToOffset(TimeSpan.FromMinutes(-90))], free: [instant.AddTicks(1)]);
    }

    [Fact]
    public async Task Composite_key_of_a_NaN_and_a_text_without_case_is_one_value_by_both_comparisons()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Edges("Utf16", "Plain"));
        var edges = database.Set("Edge");
        var otherNaN = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0001));

        await edges.InsertAsync(RecordOf(edges, 1, ("Real", double.NaN), ("Tag", "Ada")));
        await edges.InsertAsync(RecordOf(edges, 2, ("Real", -0.0), ("Tag", "Ada")));

        await UniqueSchemas.ExpectTakenAsync("ByRealAndTag", () => edges.InsertAsync(RecordOf(edges, 3, ("Real", otherNaN), ("Tag", "ADA"))));
        await UniqueSchemas.ExpectTakenAsync("ByRealAndTag", () => edges.InsertAsync(RecordOf(edges, 4, ("Real", 0.0), ("Tag", "ada"))));
        await edges.InsertAsync(RecordOf(edges, 5, ("Real", double.NaN), ("Tag", "Adb")));
        await edges.InsertAsync(RecordOf(edges, 6, ("Real", double.PositiveInfinity), ("Tag", "Ada")));

        Assert.Equal([1L], UniqueSchemas.IdsOf(edges.Index("ByRealAndTag").Find(IndexKey.Equal(otherNaN, "aDA"))));
        Assert.Equal([2L], UniqueSchemas.IdsOf(edges.Index("ByRealAndTag").Find(IndexKey.Equal(0.0, "ADA"))));
    }

    // Stores an entity with the value in the field, refuses an insert and a replace of another entity for
    // each value taken, naming the index of the field, accepts an insert for each value free, and leaves the
    // index with one owner of the value, which a search for each value taken finds as well. The ids of the
    // entities of a call count from the first given.
    private static async Task ExpectOneValueAsync(ILatticeDatabase database, long first, string field, object value, object[] taken, object[] free)
    {
        var edges = database.Set("Edge");
        var index = $"By{field}";
        var owner = first;
        var other = first + 1;
        var next = first + 2;

        await edges.InsertAsync(RecordOf(edges, owner, (field, value)));
        await edges.InsertAsync(RecordOf(edges, other));

        foreach (var equal in taken)
        {
            await UniqueSchemas.ExpectTakenAsync(index, () => edges.InsertAsync(RecordOf(edges, next++, (field, equal))));
            await UniqueSchemas.ExpectTakenAsync(index, () => edges.ReplaceAsync(RecordOf(edges, other, (field, equal))));
            Assert.Equal([owner], UniqueSchemas.IdsOf(edges.Index(index).Find(IndexKey.Equal(equal))));
        }

        foreach (var different in free)
        {
            var id = next++;

            await edges.InsertAsync(RecordOf(edges, id, (field, different)));
            Assert.Equal([id], UniqueSchemas.IdsOf(edges.Index(index).Find(IndexKey.Equal(different))));
        }

        Assert.Null(edges.Get(other)![field]);
        Assert.Equal([owner], UniqueSchemas.IdsOf(edges.Index(index).Find(IndexKey.Equal(value))));
    }

    private static LatticeRecord RecordOf(ILatticeSet set, long id, params (string Field, object? Value)[] values) => UniqueSchemas.RecordOf(set, id, values);

    // Edge, by name: Id, an Int64; A and B, Strings compared Ordinal, in the unique index ByAB (A, B); Exact,
    // a String compared Ordinal, and Folded, one compared without case, each alone in a unique index, both
    // with the storage given; Amount, a Decimal, and At, a DateTimeOffset, each alone in a unique index; Real,
    // a Float64, and Tag, a String compared without case, in the unique index ByRealAndTag. Every field but Id
    // can be null.
    private static LatticeSchema Edges(string encoding, string storage) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Edge",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "A", "Type": "String", "Nullable": true },
                { "Id": 3, "Name": "B", "Type": "String", "Nullable": true },
                { "Id": 4, "Name": "Exact", "Type": "String", "Nullable": true, "StringStorage": "{{storage}}" },
                { "Id": 5, "Name": "Folded", "Type": "String", "Nullable": true, "Comparison": "OrdinalIgnoreCase", "StringStorage": "{{storage}}" },
                { "Id": 6, "Name": "Amount", "Type": "Decimal", "Nullable": true },
                { "Id": 7, "Name": "At", "Type": "DateTimeOffset", "Nullable": true },
                { "Id": 8, "Name": "Real", "Type": "Float64", "Nullable": true },
                { "Id": 9, "Name": "Tag", "Type": "String", "Nullable": true, "Comparison": "OrdinalIgnoreCase" }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByAB", "Fields": [2, 3], "Unique": true },
                { "Id": 2, "Name": "ByExact", "Fields": [4], "Unique": true },
                { "Id": 3, "Name": "ByFolded", "Fields": [5], "Unique": true },
                { "Id": 4, "Name": "ByAmount", "Fields": [6], "Unique": true },
                { "Id": 5, "Name": "ByAt", "Fields": [7], "Unique": true },
                { "Id": 6, "Name": "ByRealAndTag", "Fields": [8, 9], "Unique": true }
              ]
            }
          ]
        }
        """);
}
