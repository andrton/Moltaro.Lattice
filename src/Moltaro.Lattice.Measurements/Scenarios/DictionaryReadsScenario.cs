using System.Diagnostics;
using System.Globalization;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The reads that Epic 1, sprint S13 measures (the planner's decision after the study, point 10): a read
// by key of an entity with a Dictionary field against the same entity with a Plain field, through the
// class, for 100,000 items of TextItems. A Dictionary read that is slower than a Plain read of the same
// length stored out of line beyond the spread of the rounds is a finding.
// - Against Plain out of line: texts of 100 bytes, 16 distinct ones; the time of one read on one thread
//   (ReadSeries), and the reads a second of 1 and of 16 threads together (ReadWindow).
// - Against Plain in the root: the reads a second of one thread for texts of 8, 16, 32, 48 and 64
//   bytes, which a Plain field keeps in the root, and of 65 and 128 bytes, which it keeps out of line;
//   where the two cross is read off them.
// - Beside a writer in the size class of the entries: 16 readers, and 7, which with the writer take the
//   8 processors of a run, alone and beside a writer that inserts
//   and deletes entities of Churn whose Data of 100 bytes takes a block of the size class of the entries
//   of texts of 100 bytes (144 bytes), so that the writer changes the metadata of a region on lines the
//   readers of the entries read: the counts of its slots and the bits of its slots (Storage.Arena,
//   Locate). The 16 entries share one or two regions; the Plain values of the same length spread over
//   some hundred.
// - Cold entries: 100,000 distinct texts of 100 bytes, one for each item, against Plain.
// Every part runs its rounds in alternating order of the two storages, a database loaded for each.
internal static class DictionaryReadsScenario
{
    public const string Name = "dictionary-reads";

    private const int Count = 100_000;

    private const int Rounds = 2;

    private const int ChurnRounds = 4;

    private const int LongText = 100;

    private const ulong KeySeed = 13;

    private const int ChurnRows = 1_000;

    private static readonly string[] PlainFirst = ["Plain", "Dictionary"];

    private static readonly string[] DictionaryFirst = ["Dictionary", "Plain"];

    public static IReadOnlyList<string> Run()
    {
        List<string> lines =
        [
            $"Items: {ReadSeries.Number(Count)} of TextItems, UTF-8; keys drawn from 1 to {ReadSeries.Number(Count)} with seed {KeySeed}, read through the class",
            ReadSeries.Described("those keys"),
            ReadWindow.Described,
            "",
            $"Against Plain out of line: texts of {LongText} bytes, 16 distinct ones",
        ];

        var sixteen = TextItems.Sixteen(LongText);

        for (var round = 0; round < Rounds; round++)
        {
            foreach (var storage in InOrder(round))
            {
                using var items = TextItems.Load(storage, Count, place => sixteen[place % 16]);

                lines.AddRange(Reads(items, $"round {round + 1}, {storage}"));
            }
        }

        lines.Add("");
        lines.Add("Against Plain in the root: the reads a second of one thread, 16 distinct texts of each length");

        int[] lengths = [8, 16, 32, 48, 64, 65, 128];

        for (var round = 0; round < Rounds; round++)
        {
            for (var index = 0; index < lengths.Length; index++)
            {
                var texts = TextItems.Sixteen(lengths[index]);

                foreach (var storage in InOrder(round + index))
                {
                    using var items = TextItems.Load(storage, Count, place => texts[place % 16]);
                    var one = ReadWindow.Run(items, items.Keys(KeySeed, ReadSeries.Reads), readers: 1, write: null);

                    lines.Add($"round {round + 1}, {lengths[index]} bytes, {storage}: {ReadWindow.PerSecond(one.Reads)} reads/s");
                }
            }
        }

        lines.Add("");
        lines.Add($"Readers beside a writer that inserts and deletes entities of Churn with Data of {LongText} bytes, the size class of the entries of texts of {LongText} bytes; texts of {LongText} bytes, 16 distinct ones; 16 readers as the brief asks, and 7, which with the writer take the 8 processors of the run");

        for (var round = 0; round < ChurnRounds; round++)
        {
            foreach (var storage in InOrder(round))
            {
                using var items = TextItems.Load(storage, Count, place => sixteen[place % 16]);
                var keys = items.Keys(KeySeed, ReadSeries.Reads);

                foreach (var readers in new[] { 16, 7 })
                {
                    var alone = ReadWindow.Run(items, keys, readers, write: null);
                    var beside = ReadWindow.Run(items, keys, readers, ChurnWriter(items));

                    lines.Add($"round {round + 1}, {storage}, {readers} readers: alone {ReadWindow.PerSecond(alone.Reads)} reads/s; beside the writer {ReadWindow.PerSecond(beside.Reads)} reads/s, {(beside.Reads / alone.Reads).ToString("F2", CultureInfo.InvariantCulture)} of alone; the writer {ReadWindow.PerSecond(beside.Writes)} writes/s");
                }
            }
        }

        lines.Add("");
        lines.Add($"Cold entries: {ReadSeries.Number(Count)} distinct texts of {LongText} bytes, one for each item");

        for (var round = 0; round < Rounds; round++)
        {
            foreach (var storage in InOrder(round))
            {
                using var items = TextItems.Load(storage, Count, place => TextItems.Distinct(place, LongText));

                lines.AddRange(Reads(items, $"round {round + 1}, {storage}"));
            }
        }

        return lines;
    }

    // Plain first in an even round, Dictionary first in an odd one.
    private static string[] InOrder(int round) => round % 2 == 0 ? PlainFirst : DictionaryFirst;

    // The time of one read on one thread, and the reads a second of 1 and of 16 threads.
    private static IEnumerable<string> Reads(TextItems items, string title)
    {
        var keys = items.Keys(KeySeed, ReadSeries.Reads);
        var latency = ReadSeries.Run($"{title}, one thread", count => ByClass(items, keys, count), writer: null);
        var one = ReadWindow.Run(items, keys, readers: 1, write: null);
        var sixteen = ReadWindow.Run(items, keys, readers: 16, write: null);

        return [latency, $"{title}: 1 thread {ReadWindow.PerSecond(one.Reads)} reads/s, 16 threads {ReadWindow.PerSecond(sixteen.Reads)} reads/s"];
    }

    // A writer that inserts an entity of Churn and deletes it again, over the same rows in turn: two
    // writes a call.
    private static Func<int> ChurnWriter(TextItems items)
    {
        var rows = Enumerable.Range(1, ChurnRows).Select(id => new TextItems.Churn { Id = id, Data = new byte[LongText] }).ToArray();
        var next = 0;

        return () =>
        {
            var row = rows[next++ % rows.Length];

            items.Churns.InsertAsync(row).AsTask().GetAwaiter().GetResult();
            items.Churns.DeleteAsync(row.Id).AsTask().GetAwaiter().GetResult();

            return 2;
        };
    }

    // The ticks of each of the first count reads, through the class.
    private static long[] ByClass(TextItems items, long[] keys, int count)
    {
        var times = new long[count];

        for (var index = 0; index < count; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var item = items.Items.Get(keys[index]);

            times[index] = Stopwatch.GetTimestamp() - started;

            if (item is null)
            {
                throw new InvalidOperationException($"The item {keys[index]} was not found.");
            }
        }

        return times;
    }
}
