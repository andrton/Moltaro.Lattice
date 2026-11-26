using System.Diagnostics;
using System.Globalization;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

// Read the completed timing files after the nodes close. Reading and formatting
// cannot change the captured timing window.
internal static class ChurnReport
{
    public static List<string> Timings(string output, ChurnReads.Result[] readers, long warm, long begin,
        long earlyEnd, long end)
    {
        var report = new List<string>();
        foreach (var role in new[] { "master", "child" })
        {
            foreach (var path in new[] { "copy", "view" })
            {
                var values = new List<long>();
                foreach (var source in readers.Where(reader => reader.Role == role && reader.Path == path))
                {
                    ReadTimes(Path.Combine(output, source.File), sizeof(long) * 2, begin, end, values);
                }

                values.Sort();
                report.Add($"{role} {path} reader p99 {Percentile(values, 99)} over {values.Count} samples");
            }

            AddTurnTimings(report, output, role, warm, begin, earlyEnd, end);
        }

        return report;
    }

    private static void AddTurnTimings(List<string> report, string output, string role, long warm, long begin,
        long earlyEnd, long end)
    {
        var attempts = new List<long>();
        (string Name, long Begin, long End, List<long> Durations)[] admittedPhases =
        [
            ("startup", long.MinValue, warm, []),
            ("warm", warm, begin, []),
            ("early", begin, earlyEnd, []),
            ("steady", earlyEnd, end, []),
            ("measured", begin, end, []),
        ];

        using var stream = File.OpenRead(Path.Combine(output, $"{role}-turns.bin"));
        using var reader = new BinaryReader(stream);
        const int width = ChurnTurns.BytesPerSample;
        if (stream.Length % width != 0)
        {
            throw new InvalidDataException($"Incomplete timing records in {role}-turns.bin.");
        }

        while (stream.Position < stream.Length)
        {
            var stamp = reader.ReadInt64();
            var elapsed = reader.ReadInt64();
            var admitted = reader.ReadBoolean();
            stream.Position += width - 2 * sizeof(long) - sizeof(bool);
            if (begin <= stamp && stamp < end)
            {
                attempts.Add(elapsed);
            }

            if (admitted)
            {
                foreach (var phase in admittedPhases)
                {
                    if (phase.Begin <= stamp && stamp < phase.End)
                    {
                        phase.Durations.Add(elapsed);
                    }
                }
            }
        }

        attempts.Sort();
        report.Add($"{role} maintenance p99 {Percentile(attempts, 99)} across {attempts.Count} attempts, {OverMillisecond(attempts)} over 1 ms");
        foreach (var (name, _, _, durations) in admittedPhases)
        {
            durations.Sort();
            report.Add($"{role} maintenance admitted {name} p50 {Percentile(durations, 50)}, " +
                $"p99 {Percentile(durations, 99)}, max {Percentile(durations, 100)}, {durations.Count} samples, " +
                $"{OverMillisecond(durations)} over 1 ms");
        }
    }

    private static int OverMillisecond(List<long> durations) =>
        durations.Count(ticks => ticks > Stopwatch.Frequency / 1000);

    // Both formats begin with an absolute timestamp and its interval in Stopwatch ticks.
    private static void ReadTimes(string file, int width, long begin, long end, List<long> durations)
    {
        using var stream = File.OpenRead(file);
        using var reader = new BinaryReader(stream);
        if (stream.Length % width != 0)
        {
            throw new InvalidDataException($"Incomplete timing records in {Path.GetFileName(file)}.");
        }

        while (stream.Position < stream.Length)
        {
            var stamp = reader.ReadInt64();
            var elapsed = reader.ReadInt64();
            stream.Position += width - 2 * sizeof(long);
            if (begin <= stamp && stamp < end)
            {
                durations.Add(elapsed);
            }
        }
    }

    private static string Percentile(List<long> values, int percentile)
    {
        if (values.Count == 0)
        {
            return "unavailable";
        }

        var rank = (int)((values.Count * (long)percentile + 99) / 100) - 1;
        var nanoseconds = values[rank] * 1_000_000_000.0 / Stopwatch.Frequency;
        return $"{nanoseconds.ToString("F1", CultureInfo.InvariantCulture)} ns";
    }
}
