using System.Diagnostics;

namespace Moltaro.Lattice.Engine.Tests.Engine.Enumeration;

// A source of the timestamps the reads of one database take for their time limit, which the test drives
// (EngineTestInputs.ReadTimestamps): every reading returns the time now and then moves it on by the step the
// test has set, so a step of a read spends inside the engine the step times the readings it makes after its
// first, whatever the machine does meanwhile. It records every reading, so that a test tells how long each
// step of a read spent from outside, and it runs, once, an action the test gives at the next reading, so that
// something happens at the very place a read takes its time. The ticks are those of Stopwatch, as the
// timestamps of the engine are. Read on the thread of the read and set by the test between two steps; the
// tests that use it read from one thread at a time.
internal sealed class ScriptedClock
{
    private readonly List<long> _readings = [];

    private long _now = Stopwatch.Frequency;

    private long _step;

    private Action? _atNextReading;

    // The source to give a database.
    public Func<long> Source => Read;

    // How much later each reading is than the one before it.
    public TimeSpan Step
    {
        set => _step = TicksOf(value);
    }

    // How many readings were taken so far.
    public int Readings => _readings.Count;

    // The ticks of Stopwatch in a time.
    public static long TicksOf(TimeSpan time) => (long)(time.Ticks * (Stopwatch.Frequency / (double)TimeSpan.TicksPerSecond));

    // The action runs at the next reading, before the reading returns, and never again.
    public void AtNextReading(Action action) => _atNextReading = action;

    // The time between the first and the last of the readings taken from the one at the index given on:
    // what the reads that took them spent inside the engine at most, counted from outside.
    public TimeSpan SpanFrom(int reading) =>
        reading >= _readings.Count ? TimeSpan.Zero : TimeSpan.FromSeconds((_readings[^1] - _readings[reading]) / (double)Stopwatch.Frequency);

    private long Read()
    {
        var now = _now;

        _now += _step;
        _readings.Add(now);

        var action = _atNextReading;

        _atNextReading = null;
        action?.Invoke();

        return now;
    }
}
