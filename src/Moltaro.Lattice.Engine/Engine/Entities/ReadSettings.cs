namespace Moltaro.Lattice.Engine.Entities;

// The read limits of one database as every read takes them (ReadLimits), made once when the database is made:
// the time limit already in ticks of the timestamp, so that no round of a step converts it, the memory limit,
// and the source of the timestamp a read takes for its time. The source is Stopwatch.GetTimestamp, read by a
// direct call; a test of the engine gives a source of its own through EngineTestInputs.ReadTimestamps, so that
// it drives the time a step spends inside the engine without the clock of the machine and without waiting
// (exception X3, docs/work/exceptions.md). Not TimeProvider, whose timestamp is a virtual call on every
// reading.
//
// Immutable, and shared by every read of its database on every thread.
internal sealed class ReadSettings
{
    // The settings of the options of a database, with the source of the timestamp a test gives, null for
    // Stopwatch.GetTimestamp.
    public ReadSettings(LatticeDatabaseOptions options, Func<long>? timestamps)
    {
        ArgumentNullException.ThrowIfNull(options);

        throw new NotSupportedException($"The read limits of a database are not made once yet; a source of timestamps is {(timestamps is null ? "not " : "")}given.");
    }

    // The options the limits come from, for the words of a refusal.
    public LatticeDatabaseOptions Options => throw new NotSupportedException();

    // Whether a read has a time limit, and so takes a timestamp at all.
    public bool LimitsTime => throw new NotSupportedException();

    // The time limit in ticks of the timestamp, at most long.MaxValue, so that a limit longer than the ticks
    // count, such as TimeSpan.MaxValue, is the most they count and nothing overflows.
    public long TimeLimitTicks => throw new NotSupportedException();

    // The memory limit of a read in bytes; null for none.
    public long? MemoryLimit => throw new NotSupportedException();

    // The timestamp now, in ticks of Stopwatch, from the source of the settings.
    public long Timestamp() => throw new NotSupportedException();
}
