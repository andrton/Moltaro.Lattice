using System.Globalization;
using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The figure that Epic 1, sprint S08 reports on the scaling of reads ("Measured", the owner's
// decision on Q42; "Acceptance, part 1"): how many reads by key the engine database makes per second
// in total, for the 100,000 users of UserDatabase, through the class, on 1, 2, 4, 8 and 16 threads
// and, when there are more than 16 logical processors, on one thread fewer than there are and on as
// many, alone and beside one writer on a thread of its own; and how many writes that writer makes
// per second meanwhile, beside readers of its own database and, as the control, beside as many
// readers of another database of the same process.
//
// A series is a window (StepWindow): it starts its readers, each on a thread of its own, and lets them
// read for a warm-up, then counts the reads every thread makes in a window of a fixed length and
// divides their sum by the length of the window. Every reader reads the keys of read-by-key, drawn with the same seed, from a
// place of its own in them, so that the readers do not read the same users at the same moment. The
// reads are not timed one by one: a series measures what all its threads read together. The threads
// are not pinned to processors, and the processor of the development machine has cores of two kinds,
// so a series can be faster or slower by the cores it was given; a figure is the result of one window,
// and the program is run three times for a figure to be compared.
//
// The writers, one in a series, each counting its writes in the window as the readers count theirs:
// - one that replaces users with the values they have, one after the other, as in read-by-key;
// - one that inserts a user of its own and deletes it again, over and over, with keys above the
//   fixture's, so that every read still finds its user and the index keeps its size: the users stay
//   100,000 or 100,001, and the index does not grow;
// - one that inserts users of its own only, so that the index grows in the window. It writes in the
//   window only, into a database of its own for the series, which it filled before the series until
//   the index has room for 1,000 users more; so the index grows after the first 1,000 inserts of
//   the window, and again whenever it is full. The bytes of the primary indexes before and after the
//   window say how often it grew.
//
// The control. Every series with a writer runs twice: once with the readers reading the database the
// writer writes, and once with as many readers reading another database of the same process, loaded
// in the same way, which the writer does not write. The readers of the other database share nothing
// of the engine with the writer, only the machine: its processors, their caches and the memory. The
// writes of the writer beside readers of its own database divided by its writes beside readers of
// the other database is the ratio the brief judges (brief of S08, "Acceptance, part 1", as restated on
// 16 November 2026); each line says how many threads ran against how many logical processors, and a
// point at which they are more than the processors is reported but not judged.
//
// A run takes about two and a half minutes on the development machine: the series take 2.5 s
// each, and every series with the writer that inserts while the index grows loads a database of its
// own.
internal static class ReadByKeyThreadsScenario
{
    public const string Name = "read-by-key-threads";

    // The inserts of the window before the index grows, in the series in which it grows.
    private const int RoomBeforeGrowth = 1_000;

    // The users the writer that inserts and deletes takes in turn.
    private const int OwnUsers = 1_000;

    public static IReadOnlyList<string> Run()
    {
        using var users = UserDatabase.Load();
        using var others = UserDatabase.Load();

        var keys = users.Keys(ReadByKeyScenario.KeySeed, ReadSeries.Reads);
        var ownUsers = Enumerable.Range(users.Count + 1, OwnUsers).Select(key => users.NewUser(key)).ToArray();
        var next = 0;

        // One insert of a user of the writer and its delete: two writes.
        int InsertAndDelete()
        {
            var user = ownUsers[next++ % ownUsers.Length];

            users.Insert(user);
            users.Delete((long)user[user.EntityType.Key.Name]!);

            return 2;
        }

        int Replace()
        {
            users.ReplaceNext();

            return 1;
        }

        List<string> lines =
        [
            users.Fixture,
            users.Loaded,
            $"The other database, which readers of the control read: {others.Loaded}",
            $"Series: reads through the class of keys drawn from 1 to {ReadSeries.Number(users.Count)} with seed {ReadByKeyScenario.KeySeed}, each reader from a place of its own in them; a warm-up of {Seconds(StepWindow.WarmUp)} s, then the reads of all readers in a window of {Seconds(StepWindow.Length)} s, in reads per second in total",
            "",
        ];

        foreach (var threads in ThreadCounts())
        {
            lines.Add($"{Threads(threads)}, alone: {Series(users, keys, threads, write: null, writesInWindowOnly: false).Line}");
            lines.AddRange(WithControl(threads, "with a writer that replaces", reading => Series(reading, keys, threads, Replace, writesInWindowOnly: false), users, others));
            lines.AddRange(WithControl(threads, "with a writer that inserts and deletes", reading => Series(reading, keys, threads, InsertAndDelete, writesInWindowOnly: false), users, others));
            lines.AddRange(WithControl(threads, "with a writer that inserts while the index grows", reading => GrowingSeries(reading == users ? null : others, keys, threads), users, others));
        }

        return lines;
    }

    // 1, 2, 4, 8 and 16, and one fewer than the logical processors and as many when they are more
    // than 16.
    private static int[] ThreadCounts() =>
        Environment.ProcessorCount > 17 ? [1, 2, 4, 8, 16, Environment.ProcessorCount - 1, Environment.ProcessorCount] : [1, 2, 4, 8, 16];

    // The series of a writer beside readers of its own database and beside readers of the other one,
    // and the line of the ratio of its writes.
    private static IEnumerable<string> WithControl(int readers, string writer, Func<UserDatabase, (string Line, long Writes)> series, UserDatabase users, UserDatabase others)
    {
        var own = series(users);
        var other = series(others);
        var threads = readers + 1;
        var processors = Environment.ProcessorCount;
        var judged = threads <= processors ? "judged" : "more threads than processors, not judged";
        var ratio = other.Writes == 0 ? "-" : ((double)own.Writes / other.Writes).ToString("F2", CultureInfo.InvariantCulture);

        return
        [
            $"{Threads(readers)}, {writer}, readers of its own database: {own.Line}",
            $"{Threads(readers)}, {writer}, readers of the other database: {other.Line}",
            $"{Threads(readers)}, {writer}: {threads} threads on {processors} logical processors ({judged}); writes beside readers of its own database / beside readers of the other database: {ratio}",
        ];
    }

    private static string Threads(int readers) => $"{readers} {(readers == 1 ? "thread" : "threads")}";

    // The series with the writer that inserts only, into a database of its own; the readers read that
    // database, or the other one when it is given.
    private static (string Line, long Writes) GrowingSeries(UserDatabase? others, long[] keys, int readers)
    {
        using var users = UserDatabase.Load();

        var next = users.FillBelowGrowth(RoomBeforeGrowth);
        var before = users.IndexBytes;

        int Insert()
        {
            users.Insert(users.NewUser(next++));

            return 1;
        }

        var (line, writes) = Series(others ?? users, keys, readers, Insert, writesInWindowOnly: true);

        return ($"{line}; the primary indexes of the writer's database held {ReadSeries.Number(before)} bytes before the window and {ReadSeries.Number(users.IndexBytes)} after it", writes);
    }

    // A series of readers of the database, beside the writer when there is one: what it measured, and
    // the writes a second of the writer.
    private static (string Line, long Writes) Series(UserDatabase reading, long[] keys, int readers, Func<int>? write, bool writesInWindowOnly)
    {
        var window = new StepWindow();

        for (var reader = 0; reader < readers; reader++)
        {
            window.Add($"Reader {reader}", new Reader(reading, keys, reader * (keys.Length / readers)));
        }

        if (write is not null)
        {
            window.Add("Writer", new Writer(write, writesInWindowOnly));
        }

        window.Start();

        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var elapsed = window.Count();

        gen0 = GC.CollectionCount(0) - gen0;
        gen1 = GC.CollectionCount(1) - gen1;
        gen2 = GC.CollectionCount(2) - gen2;

        window.Join();

        var total = window.Counted.Take(readers).Sum();
        var writes = write is null ? 0 : window.Counted[readers];
        var line = $"{PerSecond(total, elapsed)} reads/s; {ReadSeries.Number(total)} reads in {Seconds(elapsed)} s; collections gen0 {gen0}, gen1 {gen1}, gen2 {gen2}";

        return write is null
            ? (line, 0)
            : ($"{line}; the writer made {ReadSeries.Number(writes)} writes meanwhile, {PerSecond(writes, elapsed)} per second", (long)Math.Round(writes / elapsed.TotalSeconds));
    }

    private static string PerSecond(long count, TimeSpan elapsed) =>
        ReadSeries.Number((long)Math.Round(count / elapsed.TotalSeconds));

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

    // A reader of users by key through the class, from its place in the keys on.
    private struct Reader : IWindowStep
    {
        private readonly UserDatabase _users;

        private readonly long[] _keys;

        private int _index;

        public Reader(UserDatabase users, long[] keys, int place)
        {
            _users = users;
            _keys = keys;
            _index = place;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Run(bool counting, long counted)
        {
            if (_users.ByClass.Get(_keys[_index]) is null)
            {
                NotFound(_keys[_index]);
            }

            _index = _index + 1 == _keys.Length ? 0 : _index + 1;

            return 1;
        }

        private static void NotFound(long key) => throw new InvalidOperationException($"The user {key} was not found.");
    }

    // The writer of a series: it writes from the warm-up on, or in the window only, sleeping until the
    // window opens.
    private readonly struct Writer(Func<int> write, bool inWindowOnly) : IWindowStep
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Run(bool counting, long counted)
        {
            if (!counting && inWindowOnly)
            {
                Thread.Sleep(1);

                return 0;
            }

            return write();
        }
    }
}
