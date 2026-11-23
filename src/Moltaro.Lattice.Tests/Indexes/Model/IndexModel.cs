using System.Runtime.CompilerServices;
using Moltaro.Lattice.TestSupport.Text;

namespace Moltaro.Lattice.Tests.Indexes.Model;

// The model of a search of an index: the stored entities in a plain list, ordered and matched by the rules
// of the order alone, never by the engine (docs/design/ordered-index.md, "Entries and their order" and "How
// a range is walked"; docs/decisions/queries.md, Q07.3.6, "Floating-point special values"; the decision on
// strings, Q46):
// - an index orders its entries part by part, each part by its own order: ByValue, CompareTo of the .NET
//   type of the value, so numbers as numbers, an Enum as its integer, NaN first and the two zeros equal, a
//   Decimal by its value whatever its scale, a DateTimeOffset by its instant, a text by its UTF-16 code
//   units; Ordinal, the code units; WithoutCase, the code units of the simple uppercase mapping
//   (CaseRule). A null comes after every value of its part and equals another null;
// - a search matches the entries whose leading parts equal its values, a null equal to a null, and, with a
//   range, whose next part is not null and lies between its ends; a null is in no range;
// - the answer is the matches in that order, and in reverse its exact mirror, so that a null comes first.
// Entities of one key come in no order the contract states, so an answer is held as its sequence of keys
// and the set of its ids (Expect), a page as far as the contract states it (ExpectPage), and each entity
// returned as the value it was stored with, bit for bit (ExpectStored). A failure names the search and the
// first place where the answer leaves the model.
internal sealed class IndexModel
{
    private readonly IReadOnlyList<IComparer<object>> _parts;

    // A model of an index of the parts given, each by its order. An entry may hold more values than the
    // index has parts; the model reads only the first of them.
    public IndexModel(params IReadOnlyList<IComparer<object>> parts)
    {
        _parts = parts;
    }

    public static IComparer<object> ByValue { get; } = Comparer<object>.Create(static (left, right) =>
        left is string text ? Math.Sign(string.CompareOrdinal(text, (string)right)) : Math.Sign(Comparer<object>.Default.Compare(left, right)));

    public static IComparer<object> Ordinal { get; } = Comparer<object>.Create(static (left, right) =>
        Math.Sign(string.CompareOrdinal((string)left, (string)right)));

    public static IComparer<object> WithoutCase { get; } = Comparer<object>.Create(static (left, right) =>
        string.Equals((string)left, (string)right, StringComparison.Ordinal) ? 0 : Math.Sign(string.CompareOrdinal(UppercaseOf((string)left), UppercaseOf((string)right))));

    // The simple uppercase mapping of each text compared without case, kept for as long as the text lives: a
    // text of the model is compared many times, and its mapping costs its length. Texts of the same spelling
    // need no mapping.
    private static ConditionalWeakTable<string, string> Uppercase { get; } = [];

    public int Parts => _parts.Count;

    // The value as a read returns it: a DateTimeOffset at the offset zero, its instant kept
    // (docs/decisions/schema-and-types.md, "UTC instant, no retained input offset"); every other value as
    // it was given, the bits of a floating-point value and the scale of a Decimal included.
    public static object? AsRead(object? value) =>
        value is DateTimeOffset moment ? new DateTimeOffset(moment.UtcTicks, TimeSpan.Zero) : value;

    // The order of two values of a part: below zero when the first comes first.
    public int ComparePart(int part, object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null == right is null ? 0 : left is null ? 1 : -1;
        }

        return Math.Sign(_parts[part].Compare(left, right));
    }

    // The order of two keys, over the first parts given or every part of the index.
    public int CompareKeys(IReadOnlyList<object?> left, IReadOnlyList<object?> right, int? parts = null)
    {
        for (var part = 0; part < (parts ?? Parts); part++)
        {
            var order = ComparePart(part, left[part], right[part]);

            if (order != 0)
            {
                return order;
            }
        }

        return 0;
    }

    // Whether the entry matches the search.
    public bool Matches(ModelSearch search, ModelEntry entry)
    {
        for (var part = 0; part < search.Leading.Count; part++)
        {
            if (ComparePart(part, search.Leading[part], entry.Key[part]) != 0)
            {
                return false;
            }
        }

        return !search.HasRange || InRange(search.Leading.Count, entry.Key[search.Leading.Count], search.From, search.To);
    }

    // The answer of the search over the entries: its matches in the order of the index, or in the exact
    // reverse of it.
    public List<ModelEntry> Answer(IEnumerable<ModelEntry> entries, ModelSearch search)
    {
        var matches = entries.Where(entry => Matches(search, entry)).ToList();

        matches.Sort((left, right) => CompareKeys(left.Key, right.Key));

        if (search.Reverse)
        {
            matches.Reverse();
        }

        return matches;
    }

    // That the answer is the one of the model: the same keys in the same order, and the same ids.
    public void Expect(string search, IReadOnlyList<ModelEntry> expected, IReadOnlyList<ModelEntry> actual)
    {
        for (var place = 0; place < Math.Min(expected.Count, actual.Count); place++)
        {
            Check(
                CompareKeys(expected[place].Key, actual[place].Key) == 0,
                () => $"{search} returned {actual[place]} at {place}, where the model has the key of {expected[place]}.{Environment.NewLine}{Listing(actual)}");
        }

        Check(
            expected.Count == actual.Count,
            () => $"{search} returned {actual.Count} entities, not the {expected.Count} of the model; the first place it leaves the model is {Math.Min(expected.Count, actual.Count)}.{Environment.NewLine}{Listing(actual)}");

        Assert.Equal(expected.Select(entry => entry.Id).Order(), actual.Select(entry => entry.Id).Order());
    }

    // That a page of the answer, after Offset of its matches and at most Limit of them, is the one of the
    // model as far as the contract states it: the keys at every place of the page in their order; no entity
    // twice; every entity a match whose key is the key at its place. The entities of one key come in no
    // order the contract states, so when the page cuts a run of equal keys at either end, which entities of
    // that run it holds is not stated; a page that cuts no run holds the entities of the model.
    public void ExpectPage(string search, IReadOnlyList<ModelEntry> all, int offset, int? limit, IReadOnlyList<ModelEntry> actual)
    {
        var start = Math.Min(offset, all.Count);
        var end = limit is { } most ? Math.Min(all.Count, start + most) : all.Count;
        var expected = all.Skip(start).Take(end - start).ToList();

        Check(
            expected.Count == actual.Count,
            () => $"{search} after {offset} with a limit of {limit} returned {actual.Count} entities, not the {expected.Count} of the model.{Environment.NewLine}{Listing(actual)}");
        Check(actual.Count == actual.Select(entry => entry.Id).Distinct().Count(), () => $"{search} after {offset} returned an entity twice.{Environment.NewLine}{Listing(actual)}");

        for (var place = 0; place < expected.Count; place++)
        {
            var entry = actual[place];
            var match = all.SingleOrDefault(candidate => candidate.Id == entry.Id);

            Check(
                CompareKeys(expected[place].Key, entry.Key) == 0 && match is not null && CompareKeys(match.Key, entry.Key) == 0,
                () => $"{search} after {offset} returned {entry} at {place}, where the model has a match with the key of {expected[place]}.{Environment.NewLine}{Listing(actual)}");
        }

        var cutsRun = (start > 0 && start < all.Count && CompareKeys(all[start - 1].Key, all[start].Key) == 0)
            || (end > start && end < all.Count && CompareKeys(all[end - 1].Key, all[end].Key) == 0);

        if (!cutsRun)
        {
            Assert.Equal(expected.Select(entry => entry.Id).Order(), actual.Select(entry => entry.Id).Order());
        }
    }

    // That each entity of the answer holds the values it was stored with, bit for bit, as the expected
    // entities hold them: the scale of a Decimal, the sign of a zero and the payload of a NaN included.
    public static void ExpectStored(string search, IReadOnlyList<ModelEntry> expected, IReadOnlyList<ModelEntry> actual)
    {
        var byId = expected.ToDictionary(entry => entry.Id);

        foreach (var entry in actual)
        {
            var stored = byId[entry.Id];

            for (var part = 0; part < stored.Key.Count; part++)
            {
                Check(Equals(BitsOf(stored.Key[part]), BitsOf(entry.Key[part])), () => $"{search} returned {entry}, which was stored as {stored}.");
            }
        }
    }

    // That the condition holds, with the message of the failure made only when it does not: a message lists
    // the answer, and a search is checked at every place of it.
    public static void Check(bool holds, Func<string> failure)
    {
        if (!holds)
        {
            Assert.Fail(failure());
        }
    }

    private bool InRange(int part, object? value, RangeEnd? from, RangeEnd? to)
    {
        if (value is null)
        {
            return false;
        }

        if (from is not null)
        {
            var side = ComparePart(part, value, from.Value);

            if (side < 0 || (side == 0 && !from.Inclusive))
            {
                return false;
            }
        }

        if (to is not null)
        {
            var side = ComparePart(part, value, to.Value);

            if (side > 0 || (side == 0 && !to.Inclusive))
            {
                return false;
            }
        }

        return true;
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

    private static string UppercaseOf(string text) => Uppercase.GetValue(text, CaseRule.ToUpper);

    private static string Listing(IEnumerable<ModelEntry> entries)
    {
        var shown = entries.Take(40).Select(entry => entry.ToString()).ToList();

        return string.Join(Environment.NewLine, shown);
    }
}
