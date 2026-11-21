namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// The oracle of a search of an index of one part, computed from the stored values and the rules of the
// order, never from the engine (docs/decisions/queries.md, Q07.3.6, "Floating-point special values";
// docs/design/ordered-index.md, "Entries and their order"):
// - values of one field compare by CompareTo of their .NET type, numbers as numbers, an Enum as its
//   integer, a Float32 and a Float64 with NaN first, the two zeros equal; a text by its UTF-16 code units,
//   or by the comparison a test gives (without case); a null after every value and equal to a null;
// - an equal search matches the values equal to its value; a range the values between its ends, never a
//   null; the whole index every entity;
// - the answer is the matches in that order, in reverse its exact mirror. Entities of one key come in no
//   order the contract states, so an answer is compared as its sequence of values and the set of its ids,
//   and each entity returned has to hold the value it was stored with, bit for bit (Expect).
internal static class ValueOracle
{
    public static int Compare(object? left, object? right, IComparer<object>? order = null)
    {
        if (left is null || right is null)
        {
            return left is null == right is null ? 0 : left is null ? 1 : -1;
        }

        if (order is not null)
        {
            return Math.Sign(order.Compare(left, right));
        }

        return left is string text
            ? Math.Sign(string.CompareOrdinal(text, (string)right))
            : Math.Sign(Comparer<object>.Default.Compare(left, right));
    }

    // The entities whose value equals the value given, in the order of the index.
    public static List<Stored> Equal(IEnumerable<Stored> stored, object? value, IComparer<object>? order = null) =>
        Ordered(stored.Where(entity => Compare(entity.Value, value, order) == 0), reverse: false, order);

    // The entities a range finds, either end open; with both ends open, every entity of the index.
    public static List<Stored> Range(IEnumerable<Stored> stored, RangeEnd? from, RangeEnd? to, bool reverse, IComparer<object>? order = null) =>
        Ordered(stored.Where(entity => (from is null && to is null) || InRange(entity.Value, from, to, order)), reverse, order);

    // That the answer is the one of the oracle: the same values in the same order, the same ids, and each
    // entity with the value stored for its id, bit for bit.
    public static void Expect(string search, IReadOnlyList<Stored> expected, IReadOnlyList<Stored> actual, IComparer<object>? order = null)
    {
        Assert.True(
            expected.Count == actual.Count,
            $"{search} returned {actual.Count} entities, not the {expected.Count} of the oracle.{Environment.NewLine}{Listing(actual)}");

        for (var place = 0; place < expected.Count; place++)
        {
            Assert.True(
                Compare(expected[place].Value, actual[place].Value, order) == 0,
                $"{search} returned {actual[place]} at {place}, where the oracle has the value of {expected[place]}.{Environment.NewLine}{Listing(actual)}");
        }

        Assert.Equal(expected.Select(entity => entity.Id).Order(), actual.Select(entity => entity.Id).Order());

        foreach (var entity in actual)
        {
            var stored = expected.Single(candidate => candidate.Id == entity.Id);

            Assert.True(Equals(BitsOf(stored.Value), BitsOf(entity.Value)), $"{search} returned {entity}, which was stored as {stored}.");
        }
    }

    private static bool InRange(object? value, RangeEnd? from, RangeEnd? to, IComparer<object>? order)
    {
        if (value is null)
        {
            return false;
        }

        if (from is not null)
        {
            var side = Compare(value, from.Value, order);

            if (side < 0 || (side == 0 && !from.Inclusive))
            {
                return false;
            }
        }

        if (to is not null)
        {
            var side = Compare(value, to.Value, order);

            if (side > 0 || (side == 0 && !to.Inclusive))
            {
                return false;
            }
        }

        return true;
    }

    private static List<Stored> Ordered(IEnumerable<Stored> matches, bool reverse, IComparer<object>? order)
    {
        var answer = matches.ToList();

        answer.Sort((left, right) => Compare(left.Value, right.Value, order));

        if (reverse)
        {
            answer.Reverse();
        }

        return answer;
    }

    // A value with the bits that tell values of one key apart: the scale of a Decimal, the sign of a zero
    // and the payload of a NaN.
    private static object? BitsOf(object? value) => value switch
    {
        decimal number => string.Join(",", decimal.GetBits(number)),
        double number => BitConverter.DoubleToInt64Bits(number),
        float number => BitConverter.SingleToInt32Bits(number),
        _ => value,
    };

    private static string Listing(IEnumerable<Stored> entities) => string.Join(Environment.NewLine, entities);
}
