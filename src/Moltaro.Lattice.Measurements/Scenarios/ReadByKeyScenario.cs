using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Measurements.Fixture;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The figures that Epic 1, sprint S06 reports ("Measured"): the time of a read by key on the engine
// database, for the 100,000 users of UserDatabase, on one thread, alone and while a writer runs.
//
// Each series is timed as ReadSeries says, with keys drawn from the generator of the fixture, the
// same on every run. Reads by class return a FixtureUser, reads by name a record; both are given the
// key as an Int64, which neither boxes (Q51 of the open questions).
//
// The writer replaces users with the values they have, one after the other, on a thread of its own.
// No figure here judges a gate: G2 is measured in S40 under a manifest of its own.
internal static class ReadByKeyScenario
{
    public const string Name = "read-by-key";

    // The seed of the keys of the reads, apart from the seed of the fixture.
    public const ulong KeySeed = 6;

    public static IReadOnlyList<string> Run()
    {
        using var users = UserDatabase.Load();

        var keys = users.Keys(KeySeed, ReadSeries.Reads);

        return
        [
            users.Fixture,
            users.Loaded,
            ReadSeries.Described($"keys drawn from 1 to {ReadSeries.Number(users.Count)} with seed {KeySeed}"),
            "",
            ReadSeries.Run("By class, alone", count => ByClass(users.ByClass, keys, count), writer: null),
            ReadSeries.Run("By class, with a writer", count => ByClass(users.ByClass, keys, count), users.ReplaceNext),
            ReadSeries.Run("By name, alone", count => ReadSeries.ByName(users.ByName, keys, count), writer: null),
            ReadSeries.Run("By name, with a writer", count => ReadSeries.ByName(users.ByName, keys, count), users.ReplaceNext),
        ];
    }

    // The ticks of each of the first count reads, through the class.
    private static long[] ByClass(ILatticeSet<FixtureUser> users, long[] keys, int count)
    {
        var times = new long[count];

        for (var index = 0; index < count; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var user = users.Get(keys[index]);

            times[index] = Stopwatch.GetTimestamp() - started;

            if (user is null)
            {
                throw new InvalidOperationException($"The user {keys[index]} was not found.");
            }
        }

        return times;
    }
}
