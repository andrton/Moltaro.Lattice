namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

internal sealed class CollectionMaintenanceTime : TimeProvider
{
    private long _timestamp = TimeSpan.TicksPerSecond;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

    public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
}
