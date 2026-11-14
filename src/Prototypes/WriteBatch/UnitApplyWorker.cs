using System.Diagnostics;
using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal sealed class UnitApplyWorker : IDisposable
{
    private readonly ManualResetEventSlim _stop = new();
    private readonly TestThread _thread;
    private long _target = -1;

    public UnitApplyWorker(PgRunScope scope, string stream)
    {
        _thread = new TestThread(() =>
        {
            using var store = new PgBatchStore(scope, stream, SavedBaseKindEnum.Units256);
            while (!_stop.IsSet)
            {
                var sample = store.Units!.ApplyOnce();
                if (sample.Batches > 0)
                {
                    Check.That(Samples.Count < 512, "Applier sample budget exceeded.");
                    Samples.Add(sample);
                    var started = Stopwatch.GetTimestamp();
                    RemovedJournalRows += store.Units.Cleanup();
                    CleanupTicks += Stopwatch.GetTimestamp() - started;
                }
                if (Volatile.Read(ref _target) is var target && target >= 0 && store.Units.Applied >= target)
                {
                    break;
                }
                if (sample.Batches < PgUnitBase.MaximumBatches)
                {
                    _stop.Wait(20);
                }
            }
        });
    }

    // Only inspect these aggregates after Finish joins the worker.
    public List<UnitApplySample> Samples { get; } = new(64);
    public int RemovedJournalRows { get; private set; }
    public long CleanupTicks { get; private set; }

    public void Finish(long position)
    {
        Volatile.Write(ref _target, position);
        _thread.Dispose();
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Dispose();
        _stop.Dispose();
    }
}
