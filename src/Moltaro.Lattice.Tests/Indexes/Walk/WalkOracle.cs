using Moltaro.Lattice.TestSupport.Text;

namespace Moltaro.Lattice.Tests.Indexes.Walk;

// The oracle of a walk, computed from the values of the rows and the rules of the order, never from the
// engine (docs/design/ordered-index.md, "Entries and their order"; the owner's answer of 21 November 2026
// on the reverse):
// - an index orders its entries part by part in the order of its fields: A and C as numbers, B by its
//   UTF-16 code units (Ordinal), D by the code units of its simple uppercase mapping (without case,
//   CaseRule), the same in a UTF-8 and in a UTF-16 database; a null after every value of its part and
//   equal to another null;
// - a search matches the rows whose leading parts equal its values, a null equal to a null, and, with a
//   range, whose next part is not null and lies between its ends; a null is in no range;
// - the answer is the matches in that order, and in reverse the exact mirror of it, so that a null comes
//   first there. Rows of one key come in no order the contract states, so the answer is compared as
//   its sequence of keys and the set of its ids (Expect).
internal static class WalkOracle
{
    // The value of a part of the row: A, B, C or D.
    public static object? PartOf(Row row, int part) => part switch
    {
        0 => row.A,
        1 => row.B,
        2 => row.C,
        _ => row.D,
    };

    // The order of two values of a part, a null after every value.
    public static int ComparePart(int part, object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null == right is null ? 0 : left is null ? 1 : -1;
        }

        return part switch
        {
            0 => ((int)left).CompareTo((int)right),
            1 => Math.Sign(string.CompareOrdinal((string)left, (string)right)),
            2 => ((long)left).CompareTo((long)right),
            _ => Math.Sign(string.CompareOrdinal(CaseRule.ToUpper((string)left), CaseRule.ToUpper((string)right))),
        };
    }

    // The order of the keys of two rows in an index of the parts given.
    public static int CompareKeys(int parts, Row left, Row right)
    {
        for (var part = 0; part < parts; part++)
        {
            var order = ComparePart(part, PartOf(left, part), PartOf(right, part));

            if (order != 0)
            {
                return order;
            }
        }

        return 0;
    }

    // Whether the row matches the search.
    public static bool Matches(WalkShape shape, Row row)
    {
        for (var part = 0; part < shape.Leading; part++)
        {
            if (ComparePart(part, PartOf(shape.Values!, part), PartOf(row, part)) != 0)
            {
                return false;
            }
        }

        if (shape.From is null && shape.To is null)
        {
            return true;
        }

        var value = PartOf(row, shape.Leading);

        if (value is null)
        {
            return false;
        }

        if (shape.From is { } from)
        {
            var order = ComparePart(shape.Leading, value, from.Value);

            if (order < 0 || (order == 0 && !from.Inclusive))
            {
                return false;
            }
        }

        if (shape.To is { } to)
        {
            var order = ComparePart(shape.Leading, value, to.Value);

            if (order > 0 || (order == 0 && !to.Inclusive))
            {
                return false;
            }
        }

        return true;
    }

    // The answer of the search over the rows: its matches in the index order, or in its exact reverse.
    public static List<Row> Walk(IEnumerable<Row> rows, WalkShape shape)
    {
        var matches = rows.Where(row => Matches(shape, row)).ToList();

        matches.Sort((left, right) => CompareKeys(shape.Parts, left, right));

        if (shape.Reverse)
        {
            matches.Reverse();
        }

        return matches;
    }

    // That the answer is the one of the oracle: the same keys in the same order, and the same ids.
    public static void Expect(WalkShape shape, IReadOnlyList<Row> expected, IReadOnlyList<Row> actual)
    {
        Assert.True(
            expected.Count == actual.Count,
            $"{shape} returned {actual.Count} rows, not the {expected.Count} of the oracle.{Environment.NewLine}{Listing(actual)}");

        for (var place = 0; place < expected.Count; place++)
        {
            Assert.True(
                CompareKeys(shape.Parts, expected[place], actual[place]) == 0,
                $"{shape} returned {actual[place]} at {place}, where the oracle has the key of {expected[place]}.{Environment.NewLine}{Listing(actual)}");
        }

        Assert.Equal(expected.Select(row => row.Id).Order(), actual.Select(row => row.Id).Order());
    }

    // That a page of the answer, after Offset of its matches and at most Limit of them, is the one of the
    // oracle as far as the contract states it: the keys at every place of the page in their order; no row
    // twice; every row a match whose key is the key at its place. The rows of one key come in no order the
    // contract states, so when the page cuts a run of equal keys at either end, which rows of that run it
    // holds is not stated; a page that cuts no run holds the rows of the oracle.
    public static void ExpectPage(WalkShape shape, IReadOnlyList<Row> all, int offset, int? limit, IReadOnlyList<Row> actual)
    {
        var start = Math.Min(offset, all.Count);
        var end = limit is { } most ? Math.Min(all.Count, start + most) : all.Count;
        var expected = all.Skip(start).Take(end - start).ToList();

        Assert.True(
            expected.Count == actual.Count,
            $"{shape} after {offset} with a limit of {limit} returned {actual.Count} rows, not the {expected.Count} of the oracle.{Environment.NewLine}{Listing(actual)}");
        Assert.True(actual.Count == actual.Select(row => row.Id).Distinct().Count(), $"{shape} after {offset} returned a row twice.{Environment.NewLine}{Listing(actual)}");

        for (var place = 0; place < expected.Count; place++)
        {
            var row = actual[place];
            var match = all.SingleOrDefault(candidate => candidate.Id == row.Id);

            Assert.True(
                CompareKeys(shape.Parts, expected[place], row) == 0 && match is not null && CompareKeys(shape.Parts, match, row) == 0,
                $"{shape} after {offset} returned {row} at {place}, where the oracle has a match with the key of {expected[place]}.{Environment.NewLine}{Listing(actual)}");
        }

        var cutsRun = (start > 0 && start < all.Count && CompareKeys(shape.Parts, all[start - 1], all[start]) == 0)
            || (end > start && end < all.Count && CompareKeys(shape.Parts, all[end - 1], all[end]) == 0);

        if (!cutsRun)
        {
            Assert.Equal(expected.Select(row => row.Id).Order(), actual.Select(row => row.Id).Order());
        }
    }

    private static string Listing(IEnumerable<Row> rows) => string.Join(Environment.NewLine, rows);
}
