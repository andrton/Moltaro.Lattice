using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Measurements.Fixture;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

internal sealed class ChurnReads(ChurnReadBoundary? boundary = null)
{
    private const int PerRole = 4;
    private static readonly LatticeViewFunc<LatticeRecordView, long> ReadId = static view => view["Id"].Get<long>();

    private readonly List<Reader> _readers = [];
    private readonly CountdownEvent _started = new(0);
    private readonly ManualResetEventSlim _armed = new();
    private readonly CancellationTokenSource _stop = new();
    private long _traceStart;
    private long _traceEnd;
    private long _interval;

    public void Start(ILatticeSet master, ILatticeSet? child, ChurnTrace trace)
    {
        (string Role, ILatticeSet Set)[] roles = child is null ? [("master", master)] : [("master", master), ("child", child)];
        _started.Reset(roles.Length * PerRole);
        foreach (var (role, set) in roles)
        {
            for (var index = 0; index < PerRole; index++)
            {
                var reader = new Reader(role, index, index % 2 == 0, trace.MeasuredFrames);
                _readers.Add(reader);
                reader.Work = Task.Factory.StartNew(() => Run(reader, set, trace), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }

        if (!_started.Wait(TimeSpan.FromSeconds(20)))
        {
            throw new TimeoutException("Ordinary churn readers did not start.");
        }
    }

    public void Arm(long start, long end, long interval)
    {
        _traceStart = start;
        _traceEnd = end;
        _interval = interval;
        _armed.Set();
    }

    public async Task FinishAsync(bool abort)
    {
        if (abort)
        {
            _stop.Cancel();
        }

        try
        {
            await Task.WhenAll(_readers.Select(reader => reader.Work)).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException) when (abort)
        {
        }
        finally
        {
            _armed.Dispose();
            _stop.Dispose();
            _started.Dispose();
        }
    }

    public Result[] Export(string directory)
    {
        var results = new List<Result>();
        foreach (var reader in _readers)
        {
            string name = $"{reader.Role}-reader-{reader.Index}.bin";
            using (var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew))
            using (var writer = new BinaryWriter(stream))
            {
                for (var index = 0; index < reader.SampleCount; index++)
                {
                    writer.Write(reader.Stamps[index]);
                    writer.Write(reader.Values[index]);
                }
            }

            results.Add(new Result(reader.Role, reader.Index, reader.Copy ? "copy" : "view", name, reader.SampleCount,
                reader.MeasuredReads, reader.MaximumBurstLateness, reader.LateBursts, reader.LastReadFromEnd, reader.ThreadAllocatedBytes));
        }

        return [.. results];
    }

    public void WaitForTerminal(CancellationToken token) =>
        Task.WhenAll(_readers.Select(reader => reader.Work)).WaitAsync(TimeSpan.FromSeconds(40), token).GetAwaiter().GetResult();

    public ReadFence[] Progress() =>
        [.. _readers.Select(reader => new ReadFence(reader.Role, reader.Index, reader.Copy ? "copy" : "view",
            Volatile.Read(ref reader.CompletedBursts)))];

    private void Run(Reader reader, ILatticeSet set, ChurnTrace trace)
    {
        var keys = new FixtureRandom((ulong)(ChurnTrace.Seed + reader.Index));
        _started.Signal();
        _armed.Wait(_stop.Token);
        int offset = reader.Index * 2 + (reader.Role == "master" ? 1 : 2);
        long next = _traceStart;
        long measuredStart = _traceStart + trace.WarmFrames * _interval;
        long allocatedAtStart = 0;
        for (int burst = 0; burst < trace.Frames.Length; burst++)
        {
            if (burst == trace.WarmFrames)
            {
                if (boundary is not null)
                {
                    measuredStart = boundary.Join(_stop.Token);
                    next = measuredStart;
                }

                allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
            }

            long wave = burst < trace.WarmFrames ? _traceStart + burst * _interval
                : measuredStart + (burst - trace.WarmFrames) * _interval;
            long due = wave + _interval * offset / (2 * PerRole + 1);
            ChurnPacing.WaitUntil(Math.Max(due, next), _stop.Token);
            long actualBurst = Stopwatch.GetTimestamp();
            next = actualBurst + _interval / 2;
            bool measured = burst >= trace.WarmFrames;
            int selected = measured ? (burst * 17 + reader.Index * 31) % trace.ReadBurst : -1;
            for (int index = 0; index < trace.ReadBurst; index++)
            {
                long key = trace.TimedReadKey(keys.Below(trace.TimedReadKeys));
                bool sampled = index == selected;
                long start = sampled ? Stopwatch.GetTimestamp() : 0;
                long found;
                if (reader.Copy)
                {
                    bool present = set.TryGet(key, out var row, out _);
                    long elapsed = sampled ? Stopwatch.GetTimestamp() - start : 0;
                    found = present ? row!.Get<long>("Id") : 0;
                    if (sampled)
                    {
                        Save(reader, start, elapsed);
                    }
                }
                else
                {
                    bool present = set.TryView(key, ReadId, out found);
                    long elapsed = sampled ? Stopwatch.GetTimestamp() - start : 0;
                    if (!present)
                    {
                        found = 0;
                    }

                    if (sampled)
                    {
                        Save(reader, start, elapsed);
                    }
                }

                if (found != key)
                {
                    throw new InvalidOperationException("A stable corpus key had the wrong ordinary read result.");
                }
            }

            if (burst == trace.Frames.Length - 1)
            {
                long end = boundary is null ? _traceEnd : measuredStart + trace.MeasuredFrames * _interval;
                reader.LastReadFromEnd = Stopwatch.GetTimestamp() - end;
            }

            if (measured)
            {
                reader.MeasuredReads += trace.ReadBurst;
                reader.MaximumBurstLateness = Math.Max(reader.MaximumBurstLateness, actualBurst - due);
                if (actualBurst - due > _interval / 2)
                {
                    reader.LateBursts++;
                }
            }


            Volatile.Write(ref reader.CompletedBursts, burst + 1);
        }

        reader.ThreadAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart;
    }

    private static void Save(Reader reader, long stamp, long elapsed)
    {
        int index = reader.SampleCount++;
        reader.Stamps[index] = stamp;
        reader.Values[index] = elapsed;
    }

    private sealed class Reader(string role, int index, bool copy, int capacity)
    {
        public readonly string Role = role;
        public readonly int Index = index;
        public readonly bool Copy = copy;
        public readonly long[] Values = new long[capacity];
        public readonly long[] Stamps = new long[capacity];
        public Task Work = Task.CompletedTask;
        public long MeasuredReads;
        public int CompletedBursts;
        public long ThreadAllocatedBytes;
        public int SampleCount;
        public long MaximumBurstLateness;
        public int LateBursts;
        public long LastReadFromEnd;
    }

    internal sealed record Result(string Role, int Reader, string Path, string File, int Samples, long Reads,
        long MaximumBurstLatenessTicks, int LateBursts, long LastReadFromEndTicks, long ReaderThreadAllocatedBytes);

    internal sealed record ReadFence(string Role, int Reader, string Path, int CompletedBursts);
}
