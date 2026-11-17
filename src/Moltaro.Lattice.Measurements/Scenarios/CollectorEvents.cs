using System.Diagnostics.Tracing;

namespace Moltaro.Lattice.Measurements.Scenarios;

// What the garbage collector of this process did in a window of a series (WriteCallersScenario.Series):
// the bytes the collections of the young generations copied into older ones, which a collection pays
// for in time and which only objects still reachable at a collection cost. Read from the events of the
// runtime (the GC keyword of Microsoft-Windows-DotNETRuntime, GCHeapStats), which the runtime raises
// once for every collection; the listener adds them up between Start and Stop. One listener for the
// process: an EventListener that enables the runtime's events receives them for all collections.
internal sealed class CollectorEvents : EventListener
{
    private const int HeapStatsEvent = 4;

    private const EventKeywords CollectorKeyword = (EventKeywords)0x1;

    private long _promotedFromGen0;

    private long _promotedFromGen1;

    public static CollectorEvents Instance { get; } = new();

    // Starts a window: what was promoted so far is forgotten.
    public void Start()
    {
        Interlocked.Exchange(ref _promotedFromGen0, 0);
        Interlocked.Exchange(ref _promotedFromGen1, 0);
    }

    // The bytes promoted out of gen0 and out of gen1 since Start. The runtime raises its events on a
    // thread of its own, a little after each collection; a window of seconds misses at most the last one.
    public (long FromGen0, long FromGen1) Promoted() => (Interlocked.Read(ref _promotedFromGen0), Interlocked.Read(ref _promotedFromGen1));

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(eventSource, EventLevel.Informational, CollectorKeyword);
        }
    }

    // GCHeapStats: GenerationSize0, TotalPromotedSize0, GenerationSize1, TotalPromotedSize1, and on.
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventId == HeapStatsEvent && eventData.Payload is { Count: > 3 } payload)
        {
            Interlocked.Add(ref _promotedFromGen0, Convert.ToInt64(payload[1], System.Globalization.CultureInfo.InvariantCulture));
            Interlocked.Add(ref _promotedFromGen1, Convert.ToInt64(payload[3], System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
