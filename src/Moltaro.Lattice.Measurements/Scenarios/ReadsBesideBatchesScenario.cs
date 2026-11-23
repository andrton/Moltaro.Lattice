using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Measurements.Fixture;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The scenario reads-beside-batches: how long ordinary reads are held by the publication of a write batch
// (Epic 1, sprint S17, "Measured"), on a master and on a child that follows it in the same process, for the
// 100,000 users of UserDatabase, MemoryOnly. A read by key of an entity that a publication has stored but
// whose position is not published yet waits until it is (EntityTable.TakeEntry); a read of another entity
// does not wait.
//
// The writer, one thread, commits batches on the master that replace the users 1 to n with the values they
// have, every batch the same n users, at a steady rate of 50,000 members a second: a batch of 1 every 20 µs,
// of 32 every 640 µs, of 1,000 every 20 ms, of 10,000 every 200 ms. A batch is built and committed on that
// thread, and a commit that ends after the time of the next one starts the next at once, never several to
// catch up; such a commit is counted as late. The child applies every batch as the master publishes it.
//
// The readers, eight threads on the node of the series, at most half of the 16 processors of the rented
// machine: four read the members, keys drawn from 1 to n, and four read other entities, keys drawn from
// 10,001 to 100,000, which no batch writes; each reads through the class from a place of its own in its keys.
// Every read in the window is timed alone with Stopwatch.GetTimestamp, whose cost of some tens of
// nanoseconds is in the figure, into a histogram of ticks of the timer that each reader allocates before the
// series, with the exact ticks of the few reads longer than it tells apart. The reads allocate their copies,
// and a collection stops every reader for longer than a publication holds one, so a read during which a
// collection ran is counted apart and not timed: the figures are of the reads no collection stopped. A group
// of readers reports the p50, the p99, the p99.9 and the longest read; the share of its reads longer than the
// p99.9 of the same group on the same node with no batch committed, which a run measures first, since a read
// held by a publication is longer than nearly every read alone; and how many reads are longer than the
// histogram tells apart, 100 µs where the timer counts nanoseconds, since a batch of 1,000 or 10,000 holds a
// read of a member for about as long as its publication and holds each reader once a batch. The readers of
// other entities show what those counts are without a hold. The series also gives the commits of the writer
// and the time of a commit, which holds the publication.
//
// A series that is not reported runs first, so that the code of the writer is compiled before the first
// series that is. Each series is three windows of StepWindow, so that the batches of 10,000 are some thirty;
// the writer stops after each, and after the last the child is let to reach the position of the master
// before the next series. A run takes about a minute and a half on the development machine.
internal static class ReadsBesideBatchesScenario
{
    public const string Name = "reads-beside-batches";

    // The readers of each group.
    private const int Readers = 4;

    private const int MembersASecond = 50_000;

    // The windows of a reported series.
    private const int Windows = 3;

    // The first key the readers of other entities read; no batch is larger than the keys below it.
    private const int OthersFrom = 10_001;

    // The keys of each group, drawn once a series, which its readers go through in turn.
    private const int Keys = 1 << 16;

    private const ulong KeySeed = 17;

    // The ticks of the timer a histogram tells apart one by one; longer reads are kept as their exact ticks.
    private const int MostTicks = 100_000;

    private static readonly int[] Sizes = [1, 32, 1_000, 10_000];

    private static readonly TimeSpan CatchUp = TimeSpan.FromMinutes(1);

    public static IReadOnlyList<string> Run()
    {
        using var users = UserDatabase.Load(listenUri: new Uri("lattice://127.0.0.1:0"));
        using var child = OpenChild(users.ListenUri!);

        var members = Enumerable.Range(1, Sizes.Max()).Select(key => users.NewUser(key)).ToArray();
        var others = Drawn(OthersFrom, users.Count);
        var nodes = new (string Name, ILatticeSet<FixtureUser> Users)[]
        {
            ("the master", users.ByClass),
            ("the child", child.Set<FixtureUser>()),
        };

        List<string> lines =
        [
            users.Fixture,
            $"{users.Loaded}; one child following in this process",
            $"Series: {Windows} windows, each a warm-up of {Seconds(StepWindow.WarmUp)} s, then a window of {Seconds(StepWindow.Length)} s; a writer commits batches on the master that replace the users 1 to n with the values they have, {ReadSeries.Number(MembersASecond)} members a second; {Readers} readers of the members (keys from 1 to n) and {Readers} readers of other entities (keys from {ReadSeries.Number(OthersFrom)} to {ReadSeries.Number(users.Count)}) through the class on the node of the series, keys drawn with seed {KeySeed}; every read in the windows timed, a read during which a collection ran counted apart",
            "",
        ];

        _ = Series(users, child, members, others, nodes[0].Users, size: 32, windows: 1);

        foreach (var (node, set) in nodes)
        {
            var alone = Series(users, child, members, others, set, size: 0, Windows);
            var bounds = (Members: alone.Members.Percentile(0.999), Others: alone.Others.Percentile(0.999));

            lines.Add($"{Capitalized(node)}, no batch: {alone.Collections}");
            lines.Add($"{Capitalized(node)}, no batch, readers of the members (the users 1 to 32): {Described(alone.Members, bounds.Members)}");
            lines.Add($"{Capitalized(node)}, no batch, readers of other entities: {Described(alone.Others, bounds.Others)}");

            foreach (var size in Sizes)
            {
                var measured = Series(users, child, members, others, set, size, Windows);
                var of = $"{Capitalized(node)}, batches of {ReadSeries.Number(size)}";

                lines.Add($"{of}: {measured.Writer}; {measured.Collections}");
                lines.Add($"{of}, readers of the members: {Described(measured.Members, bounds.Members)}");
                lines.Add($"{of}, readers of other entities: {Described(measured.Others, bounds.Others)}");
            }

            lines.Add("");
        }

        return lines;
    }

    // The windows of the readers of the node beside the writer of batches of the size, or beside none for
    // size 0, whose readers of the members then read the users 1 to 32; afterwards the child reaches the
    // position of the master.
    private static (Times Members, Times Others, string Writer, string Collections) Series(UserDatabase users, EngineDatabase child, LatticeRecord[] members, long[] others, ILatticeSet<FixtureUser> set, int size, int windows)
    {
        var memberKeys = Drawn(1, size == 0 ? 32 : size);
        var memberTimes = Enumerable.Range(0, Readers).Select(_ => new Times()).ToArray();
        var otherTimes = Enumerable.Range(0, Readers).Select(_ => new Times()).ToArray();
        var writer = size == 0 ? null : new BatchWriter(users.Database, members, size);
        var elapsed = TimeSpan.Zero;
        var paused = TimeSpan.Zero;
        var gen0 = 0;
        var gen1 = 0;
        var gen2 = 0;

        for (var round = 0; round < windows; round++)
        {
            var window = new StepWindow();

            for (var reader = 0; reader < Readers; reader++)
            {
                window.Add($"Reader of the members {reader}", new Reader(set, memberKeys, reader * (Keys / Readers), memberTimes[reader]));
                window.Add($"Reader of other entities {reader}", new Reader(set, others, reader * (Keys / Readers), otherTimes[reader]));
            }

            if (writer is not null)
            {
                window.Add("Writer", new Writing(writer));
            }

            window.Start();

            var collections = (Gen0: GC.CollectionCount(0), Gen1: GC.CollectionCount(1), Gen2: GC.CollectionCount(2), Paused: GC.GetTotalPauseDuration());

            elapsed += window.Count();
            gen0 += GC.CollectionCount(0) - collections.Gen0;
            gen1 += GC.CollectionCount(1) - collections.Gen1;
            gen2 += GC.CollectionCount(2) - collections.Gen2;
            paused += GC.GetTotalPauseDuration() - collections.Paused;

            window.Join();
        }

        child.WaitForPositionAsync(users.Position, CatchUp).AsTask().GetAwaiter().GetResult();

        var collected = $"collections in the windows gen0 {gen0}, gen1 {gen1}, gen2 {gen2}, paused {Milliseconds(paused)} ms";

        return (Times.Merged(memberTimes), Times.Merged(otherTimes), writer?.Described(elapsed) ?? "", collected);
    }

    // A group of readers: the time of a read, the share of reads longer than the bound and how many are
    // longer than the histogram tells apart, of the reads no collection stopped; and how many a collection
    // stopped.
    private static string Described(Times times, long bound) =>
        $"p50 {Nanoseconds(times.Percentile(0.50))}, p99 {Nanoseconds(times.Percentile(0.99))}, p99.9 {Nanoseconds(times.Percentile(0.999))}, max {Nanoseconds(times.Max)}; {ReadSeries.Number(times.Count)} reads timed; "
        + $"{Percent(times.LongerThan(bound), times.Count)} longer than {Nanoseconds(bound)}, {ReadSeries.Number(times.LongerThan(MostTicks))} longer than {Nanoseconds(MostTicks)}; {ReadSeries.Number(times.Collected)} reads during a collection, not timed";

    private static EngineDatabase OpenChild(Uri masterUri)
    {
        var child = (EngineDatabase)LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = masterUri,
        });

        child.OpenAsync().AsTask().GetAwaiter().GetResult();

        return child;
    }

    // Keys drawn from first to last by the generator of the fixture with the seed, the same on every run.
    private static long[] Drawn(int first, int last)
    {
        var random = new FixtureRandom(KeySeed);
        var keys = new long[Keys];

        for (var index = 0; index < keys.Length; index++)
        {
            keys[index] = first + random.Below(last - first + 1);
        }

        return keys;
    }

    private static string Capitalized(string text) => string.Concat(text[..1].ToUpperInvariant(), text.AsSpan(1));

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

    private static string Milliseconds(TimeSpan elapsed) => elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);

    private static string Percent(long part, long whole) =>
        whole == 0 ? "-" : $"{(100.0 * part / whole).ToString("F3", CultureInfo.InvariantCulture)} %";

    private static string Nanoseconds(long ticks) =>
        $"{(ticks * 1_000_000_000.0 / Stopwatch.Frequency).ToString("N0", CultureInfo.InvariantCulture)} ns";

    private static string Microseconds(long ticks) =>
        (ticks * 1_000_000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture);

    // The times of the reads of one reader, in ticks of the timer: a histogram up to MostTicks and the exact
    // ticks of the reads longer than that, and the count of the reads during which a collection ran. Written
    // by its reader only, read after the windows.
    private sealed class Times
    {
        private readonly long[] _histogram = new long[MostTicks + 1];

        private readonly List<long> _longer = [];

        public long Count { get; private set; }

        public long Collected { get; private set; }

        public long Max => _longer.Count > 0 ? _longer.Max() : Array.FindLastIndex(_histogram, count => count > 0);

        public static Times Merged(Times[] times)
        {
            var merged = new Times();

            foreach (var one in times)
            {
                for (var ticks = 0; ticks < merged._histogram.Length; ticks++)
                {
                    merged._histogram[ticks] += one._histogram[ticks];
                }

                merged._longer.AddRange(one._longer);
                merged.Count += one.Count;
                merged.Collected += one.Collected;
            }

            merged._longer.Sort();

            return merged;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(long ticks)
        {
            if (ticks <= MostTicks)
            {
                _histogram[ticks]++;
            }
            else
            {
                _longer.Add(ticks);
            }

            Count++;
        }

        public void AddCollected() => Collected++;

        // The nearest-rank percentile, in ticks, of merged times; 0 for none.
        public long Percentile(double fraction)
        {
            var rank = Math.Max(1, (long)Math.Ceiling(fraction * Count));
            var seen = 0L;

            for (var ticks = 0; ticks < _histogram.Length; ticks++)
            {
                seen += _histogram[ticks];

                if (seen >= rank)
                {
                    return ticks;
                }
            }

            return rank - seen <= _longer.Count ? _longer[(int)(rank - seen - 1)] : 0;
        }

        public long LongerThan(long bound)
        {
            var longer = (long)_longer.Count(ticks => ticks > bound);

            for (var ticks = bound + 1; ticks < _histogram.Length; ticks++)
            {
                longer += _histogram[ticks];
            }

            return longer;
        }
    }

    // The writer of batches: what it committed in the window and how long its commits took there. Used by
    // the thread of Writing only, read after the window.
    private sealed class BatchWriter(EngineDatabase database, LatticeRecord[] members, int size)
    {
        private readonly long _interval = Stopwatch.Frequency * size / MembersASecond;

        private readonly List<long> _commits = [];

        // When the next batch is due, in ticks of the timer; 0 before the first.
        private long _due;

        private long _late;

        // Commits the next batch when it is due and returns its members, or waits towards it and returns 0.
        public int Step(bool counting)
        {
            var now = Stopwatch.GetTimestamp();

            if (now < _due)
            {
                if (_due - now > Stopwatch.Frequency / 500)
                {
                    Thread.Sleep(1);
                }
                else
                {
                    Thread.SpinWait(20);
                }

                return 0;
            }

            var batch = database.CreateWriteBatch();

            for (var member = 0; member < size; member++)
            {
                batch.Replace(members[member]);
            }

            var started = Stopwatch.GetTimestamp();
            var commit = batch.CommitAsync();

            if (commit.IsCompletedSuccessfully)
            {
                _ = commit.Result;
            }
            else
            {
                commit.AsTask().GetAwaiter().GetResult();
            }

            var ended = Stopwatch.GetTimestamp();

            _due = _due == 0 ? ended + _interval : _due + _interval;

            if (_due < ended)
            {
                _due = ended;

                if (counting)
                {
                    _late++;
                }
            }

            if (counting)
            {
                _commits.Add(ended - started);
            }

            return size;
        }

        public string Described(TimeSpan elapsed)
        {
            var sorted = _commits.Order().ToList();
            var rate = (double)MembersASecond / size;
            var committed = $"{ReadSeries.Number(sorted.Count)} committed in the window, {ReadSeries.Number((long)Math.Round(sorted.Count / elapsed.TotalSeconds))} a second against {rate.ToString("N1", CultureInfo.InvariantCulture)} due, {ReadSeries.Number(_late)} late";

            return sorted.Count == 0
                ? committed
                : $"{committed}; a commit min {Microseconds(sorted[0])} µs, median {Microseconds(sorted[sorted.Count / 2])} µs, p99 {Microseconds(sorted[Math.Max(0, (int)Math.Ceiling(0.99 * sorted.Count) - 1)])} µs, max {Microseconds(sorted[^1])} µs";
        }
    }

    // A reader of users by key through the class, from its place in its keys on, timing every read of the
    // window into its times, or counting it apart when a collection ran during it.
    private struct Reader : IWindowStep
    {
        private readonly ILatticeSet<FixtureUser> _users;

        private readonly long[] _keys;

        private readonly Times _times;

        private int _index;

        public Reader(ILatticeSet<FixtureUser> users, long[] keys, int place, Times times)
        {
            _users = users;
            _keys = keys;
            _times = times;
            _index = place;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Run(bool counting, long counted)
        {
            var collections = GC.CollectionCount(0);
            var before = Stopwatch.GetTimestamp();

            if (_users.Get(_keys[_index]) is null)
            {
                NotFound(_keys[_index]);
            }

            var took = Stopwatch.GetTimestamp() - before;

            if (counting)
            {
                if (GC.CollectionCount(0) == collections)
                {
                    _times.Add(took);
                }
                else
                {
                    _times.AddCollected();
                }
            }

            _index = _index + 1 == _keys.Length ? 0 : _index + 1;

            return 1;
        }

        private static void NotFound(long key) => throw new InvalidOperationException($"The user {key} was not found.");
    }

    // The thread of the writer of batches.
    private readonly struct Writing(BatchWriter writer) : IWindowStep
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Run(bool counting, long counted) => writer.Step(counting);
    }
}
