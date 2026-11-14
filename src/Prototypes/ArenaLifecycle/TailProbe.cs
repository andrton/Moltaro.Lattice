using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed class TailProbe
{
    #region Fixed per-thread recording storage

    [ThreadStatic] private static TailProbe? _attached;
    [ThreadStatic] private static TailProbe? _active;
    private readonly string _name;
    private readonly TailSample[] _samples = new TailSample[5000];
    private readonly GateTiming[] _gates = new GateTiming[512];
    private readonly StageTiming[] _stages = new StageTiming[16];
    private readonly int[] _stack = new int[16];
    private readonly TailCapture[] _slowest = Enumerable.Range(0, 12).Select(_ => new TailCapture()).ToArray();
    private int _count;
    private int _gateCount;
    private int _stageCount;
    private int _depth;
    private TailSample _start;

    public TailProbe(string name) => _name = name;
    public static TailProbe? Active => _active;
    public static void Attach(TailProbe? probe) => _attached = probe;

    #endregion

    #region Operation and stage boundaries

    public static void Begin(string operation, int key)
    {
        var probe = _attached;
        if (probe is null)
        {
            return;
        }

        Check.That(_active is null && probe._count < probe._samples.Length, "Tail operation budget/nesting.");
        probe._gateCount = 0;
        probe._stageCount = 0;
        probe._depth = 0;
        var cpu = ThreadClock.Read();
        probe._start = new TailSample(operation, key, 0, 0, cpu.CpuTicks, cpu.Cycles,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            GC.GetTotalPauseDuration().Ticks, 0, 0, 0);
        _active = probe;
        probe._start = probe._start with { Start = Stopwatch.GetTimestamp() };
    }

    public static void End()
    {
        var ended = Stopwatch.GetTimestamp();
        var probe = _active;
        if (probe is null)
        {
            return;
        }

        _active = null;
        var cpu = ThreadClock.Read();
        Check.That(probe._depth == 0, "Tail operation ended while holding a gate.");
        long acquire = 0, contended = 0;
        var conflicts = 0;
        for (var index = 0; index < probe._gateCount; index++)
        {
            var gate = probe._gates[index];
            acquire += gate.Acquired - gate.Start;
            if (gate.Contended && !gate.Skipped)
            {
                contended += gate.Acquired - gate.Start;
                conflicts++;
            }
        }

        var start = probe._start;
        var sample = start with
        {
            End = ended,
            CpuTicks = cpu.CpuTicks - start.CpuTicks,
            Cycles = cpu.Cycles - start.Cycles,
            Gen0 = GC.CollectionCount(0) - start.Gen0,
            Gen1 = GC.CollectionCount(1) - start.Gen1,
            Gen2 = GC.CollectionCount(2) - start.Gen2,
            GcPauseTicks = GC.GetTotalPauseDuration().Ticks - start.GcPauseTicks,
            AcquireTicks = acquire,
            ContendedAcquireTicks = contended,
            ContendedGates = conflicts
        };
        probe._samples[probe._count++] = sample;
        var target = probe._slowest[0];
        foreach (var capture in probe._slowest)
        {
            if (capture.Sample.ElapsedTicks < target.Sample.ElapsedTicks)
            {
                target = capture;
            }
        }

        if (sample.ElapsedTicks > target.Sample.ElapsedTicks)
        {
            target.Sample = sample;
            target.GateCount = probe._gateCount;
            target.StageCount = probe._stageCount;
            probe._gates.AsSpan(0, probe._gateCount).CopyTo(target.Gates);
            probe._stages.AsSpan(0, probe._stageCount).CopyTo(target.Stages);
        }
    }

    public static void Mark(string name)
    {
        var probe = _active;
        if (probe is not null)
        {
            Check.That(probe._stageCount < probe._stages.Length, "Tail stage budget.");
            probe._stages[probe._stageCount++] = new StageTiming(name, Stopwatch.GetTimestamp());
        }
    }

    #endregion

    #region Gate intervals

    public int StartGate(object gate, string kind)
    {
        Check.That(_gateCount < _gates.Length, "Tail gate budget.");
        var index = _gateCount++;
        _gates[index] = new GateTiming
        {
            Kind = kind,
            Identity = RuntimeHelpers.GetHashCode(gate),
            Start = Stopwatch.GetTimestamp()
        };
        return index;
    }

    public void Acquired(int index, bool contended, bool skipped = false)
    {
        ref var timing = ref _gates[index];
        timing.Acquired = Stopwatch.GetTimestamp();
        timing.Contended = contended;
        timing.Skipped = skipped;
        if (skipped)
        {
            timing.Released = timing.Acquired;
            return;
        }

        Check.That(_depth < _stack.Length, "Tail lock depth budget.");
        _stack[_depth++] = index;
    }

    public void Released()
    {
        Check.That(_depth > 0, "Tail lock stack underflow.");
        _gates[_stack[--_depth]].Released = Stopwatch.GetTimestamp();
    }

    #endregion

    public TailThreadReport Report()
    {
        var factor = 1_000_000.0 / Stopwatch.Frequency;
        var summaries = _samples.Take(_count).GroupBy(sample => sample.Operation).Select(group =>
        {
            var values = group.OrderBy(sample => sample.ElapsedTicks).ToArray();
            double Percentile(double fraction) => values[(int)Math.Ceiling(values.Length * fraction) - 1].ElapsedTicks * factor;
            return new TailOperationSummary(group.Key, values.Length, Percentile(0.5), Percentile(0.99),
                values[^1].ElapsedTicks * factor, values.Count(value => value.ContendedGates > 0),
                values.Count(value => value.Gen0 + value.Gen1 + value.Gen2 > 0 || value.GcPauseTicks > 0),
                values.Max(value => value.AcquireTicks) * factor, values.Max(value => value.ContendedAcquireTicks) * factor);
        }).ToArray();
        var slowest = _slowest.Where(capture => capture.Sample.End > 0)
            .OrderByDescending(capture => capture.Sample.ElapsedTicks)
            .Select(capture => new SlowOperation(capture.Sample,
                capture.Gates[..capture.GateCount], capture.Stages[..capture.StageCount])).ToArray();
        return new TailThreadReport(_name, summaries, slowest);
    }
}
