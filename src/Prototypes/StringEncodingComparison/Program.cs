using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Moltaro.Lattice.Prototypes.StringEncodingComparison;

internal static class Program
{
    private const int Count = 4096;
    private const int PositionCount = 8192;
    private const int Seed = 20261112;
    private const int NeedleIndex = 7;
    private const int Samples = 5;
    private const int Repetitions = 8;
    private static long _consumedChecksum;

    #region Experiment

    private static void Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("utf8" or "utf16"))
        {
            throw new ArgumentException("Usage: StringEncodingComparison <utf8|utf16> <ascii|cyrillic|cjk>");
        }

        var values = TextCorpus.Create(args[1], Count);
        ITextStorage storage = args[0] == "utf8"
            ? new Utf8Storage(values, values[NeedleIndex])
            : new Utf16Storage(values, values[NeedleIndex]);
        Verify(storage, values, values[NeedleIndex]);
        VerifyUnicodeCases();

        var random = new Random(Seed);
        var positions = new int[PositionCount];
        for (var index = 0; index < positions.Length; index++)
        {
            positions[index] = random.Next(Count);
        }
        positions[0] = NeedleIndex;

        var expectedText = positions.Sum(position => ValueChecksum(values[position].AsSpan()));
        var expectedMatches = positions.LongCount(position => position == NeedleIndex);
        var destination = new char[values.Max(value => value.Length)];
        var measurements = new[]
        {
            Measure("Materialize CLR string", () => ReadStrings(storage, positions), expectedText),
            Measure("Copy to reused char buffer", () => CopyChars(storage, positions, destination), expectedText),
            Measure("Exact equality, prepared needle", () => Match(storage, positions), expectedMatches)
        };

        var report = new
        {
            Encoding = args[0],
            Corpus = args[1],
            Count,
            PositionCount,
            Seed,
            Samples,
            Repetitions,
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            storage.PayloadBytes,
            SlicePayloadBytes = Count * 2L * sizeof(int),
            CorrectnessChecksPassed = true,
            Measurements = measurements
        };
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        GC.KeepAlive(storage);
        GC.KeepAlive(_consumedChecksum);
    }

    #endregion

    #region Measured operations

    private static long ReadStrings(ITextStorage storage, int[] positions)
    {
        long checksum = 0;
        foreach (var position in positions)
        {
            checksum += ValueChecksum(storage.ReadString(position).AsSpan());
        }
        return checksum;
    }

    private static long CopyChars(ITextStorage storage, int[] positions, char[] destination)
    {
        long checksum = 0;
        foreach (var position in positions)
        {
            var length = storage.CopyChars(position, destination);
            checksum += ValueChecksum(destination.AsSpan(0, length));
        }
        return checksum;
    }

    private static long Match(ITextStorage storage, int[] positions)
    {
        long matches = 0;
        foreach (var position in positions)
        {
            matches += storage.EqualsNeedle(position) ? 1 : 0;
        }
        return matches;
    }

    private static Measurement Measure(string name, Func<long> operation, long expected)
    {
        for (var warmup = 0; warmup < 3; warmup++)
        {
            RequireEqual(expected, operation());
        }

        var times = new double[Samples];
        double maximumAllocated = 0;
        for (var sample = 0; sample < Samples; sample++)
        {
            long checksum = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var repetition = 0; repetition < Repetitions; repetition++)
            {
                checksum += operation();
            }
            var ticks = Stopwatch.GetTimestamp() - start;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            RequireEqual(expected * Repetitions, checksum);
            _consumedChecksum = checksum;
            times[sample] = ticks * 1_000_000_000.0 / Stopwatch.Frequency / Repetitions / PositionCount;
            maximumAllocated = Math.Max(maximumAllocated, (double)allocated / Repetitions / PositionCount);
        }

        Array.Sort(times);
        return new Measurement(name, times[Samples / 2], times[0], times[^1], maximumAllocated, expected);
    }

    #endregion

    #region Correctness checks

    private static void VerifyUnicodeCases()
    {
        string[] values = ["", "A\0B", "\u00E9", "e\u0301", "\U0001F600", "\uE000", "\U00010000"];
        foreach (var needle in values)
        {
            Verify(new Utf8Storage(values, needle), values, needle);
            Verify(new Utf16Storage(values, needle), values, needle);
        }
    }

    private static void Verify(ITextStorage storage, string[] values, string needle)
    {
        var destination = new char[values.Max(value => value.Length)];
        for (var index = 0; index < values.Length; index++)
        {
            if (!StringComparer.Ordinal.Equals(values[index], storage.ReadString(index)))
            {
                throw new InvalidOperationException("Owned string differs from source.");
            }
            var length = storage.CopyChars(index, destination);
            if (!values[index].AsSpan().SequenceEqual(destination.AsSpan(0, length)))
            {
                throw new InvalidOperationException("Copied text differs from source.");
            }
            if (storage.EqualsNeedle(index) != StringComparer.Ordinal.Equals(values[index], needle))
            {
                throw new InvalidOperationException("Equality differs from Ordinal semantics.");
            }
        }
    }

    private static long ValueChecksum(ReadOnlySpan<char> value)
    {
        return value.IsEmpty ? 0 : value.Length + value[0] + value[^1];
    }

    private static void RequireEqual(long expected, long actual)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException($"Checksum differs: {expected} versus {actual}.");
        }
    }

    #endregion
}
