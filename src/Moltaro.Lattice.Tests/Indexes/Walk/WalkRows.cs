namespace Moltaro.Lattice.Tests.Indexes.Walk;

// The rows of the walk tests and the searches over them.
//
// The values of each field are few, so that many rows share a key and every leading part has runs of
// rows, and a null is among the values of every part, so that it stands in every position of a composite
// key. They are chosen where an order is easy to get wrong: the extremes of a signed number, whose stored
// bytes do not sort as the number; an empty text; texts that differ only in case, one key without case
// and two with Ordinal; a character of U+E000 to U+FFFF and one beyond U+FFFF, whose UTF-8 bytes sort the
// other way round from their UTF-16 code units; and two texts longer than the 64 bytes an entry holds a
// copy of, which an entry shares with the text entry of its root (docs/design/ordered-index.md, "Text
// keys"). The rows come from a generator of a fixed seed, so every run has the same rows.
internal static class WalkRows
{
    public const int Count = 240;

    // The most distinct keys of the leading parts the searches take for one count of leading parts.
    private const int MostKeys = 24;

    public static readonly string LongFirst = new string('x', 70) + "1";

    public static readonly string LongSecond = new string('x', 70) + "2";

    private static readonly int?[] AValues = [null, int.MinValue, -1, 0, 7, int.MaxValue];

    private static readonly string?[] BValues = [null, "", "a", "A", "b", "ab", "～", "\U0001F600", LongFirst, LongSecond];

    private static readonly long?[] CValues = [null, long.MinValue, -1, 0, 5, long.MaxValue];

    private static readonly string?[] DValues = [null, "apple", "APPLE", "Banana", "banana", "é", "É", "z"];

    // The values each field takes in some range bound: those of the rows, and values between them and
    // beyond them.
    private static readonly object[][] BoundValues =
    [
        [int.MinValue, -5, -1, 0, 3, 7, int.MaxValue],
        ["", "A", "a", "aa", "b", "～", "\U0001F600", LongFirst, "zzz"],
        [long.MinValue, -1L, 0L, 1L, 5L, long.MaxValue],
        ["APPLE", "apple", "b", "BANANA", "é", "zz"],
    ];

    public static IReadOnlyList<Row> All { get; } = Generate();

    // The searches of the tests of order and bounds over the rows, in both directions: every index; every
    // count of leading parts, each with the values of every distinct key the rows have for those parts and
    // with one key no row has; and with fewer leading parts than the index has, the search without a range
    // and with ranges on the next part, inclusive and exclusive, either end open.
    public static IEnumerable<WalkShape> Shapes()
    {
        for (var parts = 1; parts <= 4; parts++)
        {
            for (var leading = 0; leading <= parts; leading++)
            {
                foreach (var values in LeadingValues(leading))
                {
                    foreach (var (from, to) in Ranges(parts, leading))
                    {
                        yield return new WalkShape(parts, leading, values, from, to, Reverse: false);
                        yield return new WalkShape(parts, leading, values, from, to, Reverse: true);
                    }
                }
            }
        }
    }

    // A row of the values of the fields given, for a test of its own.
    public static Row Of(long id, int? a = null, string? b = null, long? c = null, string? d = null) =>
        new() { Id = id, A = a, B = b, C = c, D = d };

    private static List<Row> Generate()
    {
        var random = new Random(15);
        var rows = new List<Row>();

        for (var id = 1L; id <= Count; id++)
        {
            rows.Add(new Row
            {
                Id = id,
                A = AValues[random.Next(AValues.Length)],
                B = BValues[random.Next(BValues.Length)],
                C = CValues[random.Next(CValues.Length)],
                D = DValues[random.Next(DValues.Length)],
            });
        }

        return rows;
    }

    // The rows whose values give the leading parts of a search: distinct keys of the rows in those parts, at
    // most MostKeys of them spread over the rows, and one more whose key no row has.
    private static IEnumerable<Row?> LeadingValues(int leading)
    {
        if (leading == 0)
        {
            yield return null;

            yield break;
        }

        var distinct = new List<Row>();

        foreach (var row in All)
        {
            if (!distinct.Any(seen => WalkOracle.CompareKeys(leading, seen, row) == 0))
            {
                distinct.Add(row);
            }
        }

        var every = Math.Max(1, distinct.Count / MostKeys);

        for (var place = 0; place < distinct.Count; place += every)
        {
            yield return distinct[place];
        }

        yield return Of(0, a: 3, b: "none", c: 2, d: "none");
    }

    // The ranges of a search of the leading parts given on an index of the parts given: none, and when a part
    // follows the leading ones, ranges over it from its bound values.
    private static IEnumerable<(RangeEnd? From, RangeEnd? To)> Ranges(int parts, int leading)
    {
        yield return (null, null);

        if (leading == parts)
        {
            yield break;
        }

        var values = BoundValues[leading];
        var low = values[1];
        var high = values[^2];

        foreach (var value in values)
        {
            yield return (RangeEnd.Including(value), null);
            yield return (RangeEnd.Excluding(value), null);
            yield return (null, RangeEnd.Including(value));
            yield return (null, RangeEnd.Excluding(value));
        }

        yield return (RangeEnd.Including(low), RangeEnd.Including(high));
        yield return (RangeEnd.Excluding(low), RangeEnd.Excluding(high));
        yield return (RangeEnd.Including(low), RangeEnd.Excluding(low));
        yield return (RangeEnd.Including(low), RangeEnd.Including(low));
        yield return (RangeEnd.Including(high), RangeEnd.Including(low));
    }
}
