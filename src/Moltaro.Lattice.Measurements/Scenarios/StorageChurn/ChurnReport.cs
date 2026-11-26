using System.Diagnostics;
using System.Globalization;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

// Read the completed timing files after the nodes close. Reading and formatting
// cannot change the captured timing window.
internal static class ChurnReport
{
    public static List<string> Timings(string output, ChurnReads.Result[] readers, long begin, long end)
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

                report.Add($"{role} {path} reader p99 {P99(values)} over {values.Count} samples");
            }

            var turns = new List<long>();
            ReadTimes(Path.Combine(output, $"{role}-turns.bin"), ChurnTurns.BytesPerSample, begin, end, turns);
            var over = turns.Count(ticks => ticks * 1000 > Stopwatch.Frequency);
            report.Add($"{role} maintenance p99 {P99(turns)} across {turns.Count} attempts, {over} over 1 ms");
        }

        return report;
    }

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

    private static string P99(List<long> values)
    {
        if (values.Count == 0)
        {
            return "unavailable";
        }

        values.Sort();
        var rank = (int)((values.Count * 99L + 99) / 100) - 1;
        var nanoseconds = values[rank] * 1_000_000_000.0 / Stopwatch.Frequency;
        return $"{nanoseconds.ToString("F1", CultureInfo.InvariantCulture)} ns";
    }
}
