using System.Diagnostics;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed class BackgroundApplyWorker : IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private Exception? _failure;

    public BackgroundApplyWorker(string connectionString, int unitBytes, int maximumBatches,
        int intervalMilliseconds, ReplayPatternEnum pattern, ApplyRunState state, CancellationToken token, ReplayLayout layout)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _thread = new Thread(() =>
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                using var session = new BackgroundApplySession(connectionString, unitBytes, maximumBatches, pattern, layout);
                Session = session;
                state.AppliedThrough(session.Progress);
                _ready.Set();
                while (!_cancellation.IsCancellationRequested)
                {
                    var started = Stopwatch.GetTimestamp();
                    var applied = session.ApplyOnce(_cancellation.Token);
                    state.AppliedThrough(session.Progress);
                    if (state.WriterFinished && session.Progress == state.Published)
                    {
                        break;
                    }

                    var full = session.Workspace.JournalBatches == maximumBatches || session.Workspace.HitUnitBudget;
                    var wait = state.WriterFinished || full ? 0 : intervalMilliseconds;
                    if (!applied)
                    {
                        wait = Math.Max(5, wait);
                    }

                    ArrivalScheduler.WaitUntil(started + (long)wait * Stopwatch.Frequency / 1000, _cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                // A transaction already executing completes before the next cancellation check.
            }
            catch (Exception exception)
            {
                _failure = exception;
            }
            finally
            {
                AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                _ready.Set();
            }
        }) { IsBackground = true, Name = "Lattice background journal application" };
        _thread.Start();
        try
        {
            _ready.Wait(token);
            ThrowIfFailed();
        }
        catch
        {
            _cancellation.Cancel();
            _thread.Join();
            _ready.Dispose();
            _cancellation.Dispose();
            throw;
        }
    }

    public BackgroundApplySession? Session { get; private set; }
    public long AllocatedBytes { get; private set; }
    public bool Finished => !_thread.IsAlive;

    public void ThrowIfFailed()
    {
        if (_failure is not null)
        {
            throw new InvalidOperationException("Background application failed.", _failure);
        }
    }

    public void Stop()
    {
        _cancellation.Cancel();
        _thread.Join();
        ThrowIfFailed();
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _thread.Join();
        _ready.Dispose();
        _cancellation.Dispose();
    }
}
