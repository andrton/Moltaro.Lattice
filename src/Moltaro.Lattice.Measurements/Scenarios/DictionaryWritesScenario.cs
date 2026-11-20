using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The writes that Epic 1, sprint S13 measures (the planner's decision after the study, point 10), for
// 100,000 items of TextItems with a field of Plain or of Dictionary storage, texts of 9 bytes, which a
// Plain field keeps in the root, and of 100 bytes, which it keeps out of line:
// - Writes a second: callers on threads of their own, 1 and 4, each replacing items of its own, each
//   replace moving its item between two texts that other items hold, so that every Dictionary write
//   finds its text in the table; a warm-up, then a window (StepWindow), as write-callers counts.
// - Each kind of write on one thread, 100,000 of it: a replace that keeps the text, one that moves to a
//   text the database holds (a hit), one to a text it does not hold (a miss), and a delete; the time of
//   one write and the managed memory its thread allocated, in the mean over the 100,000. In MemoryOnly a
//   write runs on the thread of its call, so that thread's allocation is the write's.
// - A drain whose roots hold references: while a read is held inside the protection (HoldNextRead),
//   every item is replaced, so that 100,000 roots are retired and wait; the read leaves, and one drain
//   (DrainRetired) frees them, and with a Dictionary field releases a reference for each.
// - One repeated text of 100 KiB: 1,000 items that hold it, the time to insert each, the time to read
//   each, and the bytes the arena retains for them.
// Every part runs its rounds in alternating order of the two storages, a database loaded for each.
internal static class DictionaryWritesScenario
{
    public const string Name = "dictionary-writes";

    private const int Count = 100_000;

    private const int Rounds = 2;

    private static readonly int[] Lengths = [9, 100];

    private static readonly string[] PlainFirst = ["Plain", "Dictionary"];

    private static readonly string[] DictionaryFirst = ["Dictionary", "Plain"];

    public static IReadOnlyList<string> Run()
    {
        List<string> lines =
        [
            $"Items: {ReadSeries.Number(Count)} of TextItems, UTF-8, 16 distinct texts of each length unless a part says otherwise",
            $"Windows of the callers: a warm-up of {Seconds(StepWindow.WarmUp)} s, then the writes of all callers in {Seconds(StepWindow.Length)} s, per second",
            "",
            "Writes a second, replaces between two texts the database holds",
        ];

        foreach (var length in Lengths)
        {
            for (var round = 0; round < Rounds; round++)
            {
                foreach (var storage in InOrder(round))
                {
                    using var items = Load(storage, length);
                    var one = Callers(items, length, callers: 1);
                    var four = Callers(items, length, callers: 4);

                    lines.Add($"{length} bytes, round {round + 1}, {storage}: 1 caller {StepWindow.PerSecond(one)} writes/s, 4 callers {StepWindow.PerSecond(four)} writes/s");
                }
            }
        }

        lines.Add("");
        lines.Add($"Each kind of write on one thread, {ReadSeries.Number(Count)} of it: the mean time of one and the managed bytes its thread allocated for one");

        foreach (var length in Lengths)
        {
            for (var round = 0; round < Rounds; round++)
            {
                foreach (var storage in InOrder(round))
                {
                    using var items = Load(storage, length);

                    lines.Add($"{length} bytes, round {round + 1}, {storage}: {Kinds(items, length)}");
                }
            }
        }

        lines.Add("");
        lines.Add($"A drain of {ReadSeries.Number(Count)} retired roots, freed after a held read left");

        foreach (var length in Lengths)
        {
            for (var round = 0; round < Rounds; round++)
            {
                foreach (var storage in InOrder(round))
                {
                    using var items = Load(storage, length);

                    lines.Add($"{length} bytes, round {round + 1}, {storage}: {Drain(items, length)}");
                }
            }
        }

        lines.Add("");
        lines.Add("One repeated text of 100 KiB, 102,400 bytes of ASCII, held by 1,000 items");

        for (var round = 0; round < Rounds; round++)
        {
            foreach (var storage in InOrder(round))
            {
                lines.Add($"round {round + 1}, {storage}: {Repeated(storage)}");
            }
        }

        return lines;
    }

    private static string[] InOrder(int round) => round % 2 == 0 ? PlainFirst : DictionaryFirst;

    private static TextItems Load(string storage, int length)
    {
        var texts = TextItems.Sixteen(length);

        return TextItems.Load(storage, Count, place => texts[place % 16]);
    }

    // The writes a second of the callers together, each on a thread of its own replacing the items whose
    // place leaves its own number when divided by the number of callers.
    private static double Callers(TextItems items, int length, int callers)
    {
        var texts = TextItems.Sixteen(length);
        var window = new StepWindow();

        for (var caller = 0; caller < callers; caller++)
        {
            window.Add($"Caller {caller}", new Caller(items, texts, caller, callers));
        }

        window.Start();

        var elapsed = window.Count().TotalSeconds;

        window.Join();

        return window.Counted.Sum() / elapsed;
    }

    // The four kinds of write over every item, in this order: keep, hit, miss, delete.
    private static string Kinds(TextItems items, int length)
    {
        var texts = TextItems.Sixteen(length);
        var kept = Pass(items.Count, place => items.Items.ReplaceAsync(new TextItems.Item { Id = place + 1, Number = 1, Text = texts[place % 16] }));
        var hit = Pass(items.Count, place => items.Items.ReplaceAsync(new TextItems.Item { Id = place + 1, Number = 2, Text = texts[(place + 1) % 16] }));
        var missed = Enumerable.Range(0, items.Count).Select(place => TextItems.Distinct(place, length)).ToArray();
        var miss = Pass(items.Count, place => items.Items.ReplaceAsync(new TextItems.Item { Id = place + 1, Number = 3, Text = missed[place] }));
        var deleted = Pass(items.Count, place => items.Items.DeleteAsync((long)place + 1));

        return $"keep {kept}; hit {hit}; miss {miss}; delete {deleted}";
    }

    // count writes on this thread: the mean time and the managed bytes allocated for one.
    private static string Pass(int count, Func<int, ValueTask<LatticeWriteResult>> write)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();

        for (var place = 0; place < count; place++)
        {
            write(place).AsTask().GetAwaiter().GetResult();
        }

        var elapsed = Stopwatch.GetElapsedTime(started);

        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        return $"{(elapsed.TotalNanoseconds / count).ToString("F0", CultureInfo.InvariantCulture)} ns and {((double)allocated / count).ToString("F0", CultureInfo.InvariantCulture)} bytes";
    }

    // The drain of the roots that every item's replace retired while a read was held.
    private static string Drain(TextItems items, int length)
    {
        var texts = TextItems.Sixteen(length);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        items.Database.HoldNextRead(_ =>
        {
            entered.Set();
            release.Wait();
        });

        var reader = new Thread(() => items.Items.Get(1L)) { IsBackground = true, Name = "Held reader" };

        reader.Start();
        entered.Wait();

        for (var place = 0; place < items.Count; place++)
        {
            items.Items.ReplaceAsync(new TextItems.Item { Id = place + 1, Number = 1, Text = texts[(place + 1) % 16] }).AsTask().GetAwaiter().GetResult();
        }

        var retired = items.Database.Counters.Arena.RetiredBytes;

        release.Set();
        reader.Join();

        var started = Stopwatch.GetTimestamp();

        items.Database.DrainRetired();

        var elapsed = Stopwatch.GetElapsedTime(started);
        var left = items.Database.Counters.Arena.RetiredBytes;

        return $"{ReadSeries.Number(retired)} retired bytes freed in {elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)} ms, {ReadSeries.Number(left)} left retired";
    }

    // The items that hold one text of 100 KiB: inserted, read, and the bytes they retain.
    private static string Repeated(string storage)
    {
        const int Holders = 1_000;
        const int Reads = 4_000;

        var text = TextItems.Distinct(0, 102_400);
        var started = Stopwatch.GetTimestamp();

        using var items = TextItems.Load(storage, Holders, _ => text);

        var insert = Stopwatch.GetElapsedTime(started);
        var retained = items.RetainedBytes();
        var reading = Stopwatch.GetTimestamp();

        for (var read = 0; read < Reads; read++)
        {
            if (items.Items.Get((long)(read % Holders) + 1)?.Text.Length != text.Length)
            {
                throw new InvalidOperationException("The text of 100 KiB did not read back.");
            }
        }

        var read100 = Stopwatch.GetElapsedTime(reading);
        var counters = items.Database.Counters;

        return $"insert {(insert.TotalMicroseconds / Holders).ToString("F1", CultureInfo.InvariantCulture)} µs each (with the open), read {(read100.TotalMicroseconds / Reads).ToString("F1", CultureInfo.InvariantCulture)} µs each; arena retains {ReadSeries.Number(retained)} bytes, {ReadSeries.Number(retained / Holders)} per item; text entries {counters.TextEntries}, text table {ReadSeries.Number(counters.TextTableBytes)} bytes; one value of the text out of line takes {ReadSeries.Number(StoredSize(102_400))} bytes of slots";
    }

    private static long StoredSize(int bytes) => ValueBlocks.SlotBytesOf(bytes);

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

    // A caller that replaces the items of its own in turn, from the first again after the last, each
    // replace moving its item between the first two texts, each held by thousands of other items.
    private struct Caller : IWindowStep
    {
        private readonly TextItems _items;

        private readonly string[] _texts;

        private readonly int _caller;

        private readonly int _callers;

        private int _place;

        private long _turn;

        public Caller(TextItems items, string[] texts, int caller, int callers)
        {
            _items = items;
            _texts = texts;
            _caller = caller;
            _callers = callers;
            _place = caller;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Run(bool counting, long counted)
        {
            if (_place >= _items.Count)
            {
                _place = _caller;
                _turn++;
            }

            var item = new TextItems.Item { Id = _place + 1, Number = _turn, Text = _texts[(int)((_place + _turn) % 2)] };

            _items.Items.ReplaceAsync(item).AsTask().GetAwaiter().GetResult();
            _place += _callers;

            return 1;
        }
    }
}
