namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

// Pauses the four master readers between their warm and measured bursts, at the same cycle fence
// as the writer. The measured cadence resumes from a common start after the public census.
internal sealed class ChurnReadBoundary : IDisposable
{
    private readonly CountdownEvent _warmReaders = new(4);
    private readonly ManualResetEventSlim _measured = new();

    public long MeasuredStart { get; private set; }

    public void FinishWarm(CancellationToken token)
    {
        ChurnAudit.Require(_warmReaders.Wait(TimeSpan.FromSeconds(40), token), "The warm readers did not finish.");
    }

    public void Resume(long start)
    {
        MeasuredStart = start;
        _measured.Set();
    }

    public long Join(CancellationToken token)
    {
        _warmReaders.Signal();
        ChurnAudit.Require(_measured.Wait(TimeSpan.FromSeconds(40), token), "The measured reader schedule did not start.");
        return MeasuredStart;
    }

    public void Dispose()
    {
        _measured.Dispose();
        _warmReaders.Dispose();
    }
}
