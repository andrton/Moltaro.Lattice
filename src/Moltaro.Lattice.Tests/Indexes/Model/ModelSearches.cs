namespace Moltaro.Lattice.Tests.Indexes.Model;

// The searches a model test makes over what it stored, in both directions, for an index of any number of
// parts:
// - every count of leading parts, from none to all: each with the values of the distinct keys the entities
//   have in those parts, all of them or at most a number spread over the order, and with keys no entity
//   has, a value of the last leading part the test never stores;
// - with fewer leading parts than the index has, also ranges on the next part from bound values: values
//   the entities have there, spread over the order from the least to the greatest, and values never
//   stored, between the stored ones or beyond either end; each bound inclusive and exclusive as the lower
//   and as the upper end with the other open, pairs of a low and a high bound in all four kinds, an empty
//   range, a range of one value and an inverted one; and for no leading part the whole index;
// - pages, for Offset and Limit: the whole index, a range from the middle value of the first part, and the
//   leading search of the most frequent value of the first part.
internal static class ModelSearches
{
    // The stored values of a part that serve as bounds of a range.
    private const int SpreadBounds = 6;

    public static List<ModelSearch> Of(IndexModel model, IReadOnlyList<ModelEntry> stored, IReadOnlyList<IReadOnlyList<object>> neverStored, int mostKeys)
    {
        var searches = new List<ModelSearch>();

        for (var leading = 0; leading <= model.Parts; leading++)
        {
            foreach (var values in LeadingValues(model, stored, neverStored, leading, mostKeys))
            {
                searches.AddRange(BothWays(values, null, null));

                if (leading < model.Parts)
                {
                    foreach (var (from, to) in Ranges(model, leading, BoundsOf(model, stored, neverStored, leading)))
                    {
                        searches.AddRange(BothWays(values, from, to));
                    }
                }
            }
        }

        return searches;
    }

    public static List<ModelSearch> Pages(IndexModel model, IReadOnlyList<ModelEntry> stored)
    {
        var firsts = stored.Select(entry => entry.Key[0]).Where(value => value is not null).Cast<object>().ToList();

        firsts.Sort((left, right) => model.ComparePart(0, left, right));

        var middle = firsts[firsts.Count / 2];
        var mostFrequent = firsts[0];
        var longestRun = 0;
        var run = 0;

        for (var place = 0; place < firsts.Count; place++)
        {
            run = place > 0 && model.ComparePart(0, firsts[place - 1], firsts[place]) == 0 ? run + 1 : 1;

            if (run > longestRun)
            {
                (mostFrequent, longestRun) = (firsts[place], run);
            }
        }

        return [.. BothWays([], null, null), .. BothWays([], RangeEnd.Including(middle), null), .. BothWays([mostFrequent], null, null)];
    }

    public static IEnumerable<ModelSearch> BothWays(IReadOnlyList<object?> values, RangeEnd? from, RangeEnd? to) =>
        [new ModelSearch(values, from, to, Reverse: false), new ModelSearch(values, from, to, Reverse: true)];

    public static IEnumerable<(RangeEnd? From, RangeEnd? To)> OneSidedRanges(object value) =>
    [
        (RangeEnd.Including(value), null), (RangeEnd.Excluding(value), null),
        (null, RangeEnd.Including(value)), (null, RangeEnd.Excluding(value)),
    ];

    // The values of the leading parts of the searches: the distinct keys of the entities in those parts, at
    // most mostKeys of them spread over the order, and for each value never stored of the last leading part
    // the first of those keys with that value in its place.
    private static IEnumerable<IReadOnlyList<object?>> LeadingValues(IndexModel model, IReadOnlyList<ModelEntry> stored, IReadOnlyList<IReadOnlyList<object>> neverStored, int leading, int mostKeys)
    {
        if (leading == 0)
        {
            yield return [];

            yield break;
        }

        var sorted = stored.ToList();

        sorted.Sort((left, right) => model.CompareKeys(left.Key, right.Key, leading));

        var distinct = new List<IReadOnlyList<object?>>();

        foreach (var entry in sorted)
        {
            if (distinct.Count == 0 || model.CompareKeys(distinct[^1], entry.Key, leading) != 0)
            {
                distinct.Add([.. entry.Key.Take(leading)]);
            }
        }

        foreach (var values in Spread(distinct, mostKeys))
        {
            yield return values;
        }

        foreach (var value in neverStored[leading - 1])
        {
            yield return [.. distinct[0].Take(leading - 1), value];
        }
    }

    // The bound values of a range on the part given: values the entities have there, spread from the least
    // to the greatest, with the values never stored, in the order of the part.
    private static List<object> BoundsOf(IndexModel model, IReadOnlyList<ModelEntry> stored, IReadOnlyList<IReadOnlyList<object>> neverStored, int part)
    {
        var values = stored.Select(entry => entry.Key[part]).Where(value => value is not null).Cast<object>().ToList();

        values.Sort((left, right) => model.ComparePart(part, left, right));

        var distinct = new List<object>();

        foreach (var value in values)
        {
            if (distinct.Count == 0 || model.ComparePart(part, distinct[^1], value) != 0)
            {
                distinct.Add(value);
            }
        }

        var bounds = Spread(distinct, SpreadBounds).Concat(neverStored[part]).ToList();

        bounds.Sort((left, right) => model.ComparePart(part, left, right));

        return bounds;
    }

    private static IEnumerable<(RangeEnd? From, RangeEnd? To)> Ranges(IndexModel model, int part, List<object> bounds)
    {
        foreach (var range in bounds.SelectMany(OneSidedRanges))
        {
            yield return range;
        }

        var low = bounds[Math.Min(1, bounds.Count - 1)];
        var high = bounds[Math.Max(0, bounds.Count - 2)];

        if (model.ComparePart(part, low, high) >= 0)
        {
            (low, high) = (bounds[0], bounds[^1]);
        }

        yield return (RangeEnd.Including(low), RangeEnd.Including(high));
        yield return (RangeEnd.Excluding(low), RangeEnd.Excluding(high));
        yield return (RangeEnd.Including(low), RangeEnd.Excluding(high));
        yield return (RangeEnd.Excluding(low), RangeEnd.Including(high));
        yield return (RangeEnd.Including(low), RangeEnd.Excluding(low));
        yield return (RangeEnd.Including(low), RangeEnd.Including(low));
        yield return (RangeEnd.Including(high), RangeEnd.Including(low));
    }

    // At most the number given of the items, spread evenly from the first to the last; all of them when they
    // are no more.
    private static List<T> Spread<T>(IReadOnlyList<T> items, int most)
    {
        if (items.Count <= most)
        {
            return [.. items];
        }

        return [.. Enumerable.Range(0, most).Select(place => items[(int)((long)place * (items.Count - 1) / (most - 1))])];
    }
}
