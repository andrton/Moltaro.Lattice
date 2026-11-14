using Kaos.Collections;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison;

internal static class ApiChecks
{
    public static RangeApiResult Range()
    {
        var map = new RankedMap<long, ulong>();
        for (var index = 0; index < 300; index++)
        {
            map.Add(index, (ulong)index + 1);
        }

        int? betweenCount = null;
        string? betweenError = null;
        try
        {
            betweenCount = map.ElementsBetween(10, 299).ToArray().Length;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            betweenError = exception.GetType().Name;
        }

        var chosen = map.ElementsFrom(10).TakeWhile(entry => entry.Key <= 299).ToArray();
        Check.Require(chosen.Length == 290 && chosen.Select(entry => entry.Key).SequenceEqual(
            Enumerable.Range(10, 290).Select(value => (long)value)), "Chosen lower-bound range path failed.");
        return new(290, betweenCount, betweenError, chosen.Length, true);
    }
}
