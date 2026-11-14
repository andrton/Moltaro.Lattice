using System.Diagnostics;

namespace Moltaro.Lattice.Prototypes.OperationRetry;

internal sealed class RetryChecks(PgRetryScope scope)
{
    private int _case;
    private readonly List<string> _passed = [];

    public object Run()
    {
        LostReplies();
        ConcurrentDuplicates();
        RollbackAndReopen();
        ExpiryAndConfiguration();
        NoOpAndRejection();
        CleanupRaces();
        HistoryAndUnknown();
        var samples = MeasureSmallWrites();
        return new { Checks = _passed, Measurements = samples };
    }

    private (RetryStore Store, OperationToken Token, string Stream) Fixture()
    {
        var stream = "case_" + ++_case;
        var store = new RetryStore(scope, stream);
        var history = Guid.NewGuid();
        store.Initialize(history);
        var epoch = store.CreateEpoch(0);
        return (store, new OperationToken(history, epoch, Guid.NewGuid()), stream);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    private void Passed(string name) => _passed.Add(name);

    private void LostReplies()
    {
        var (store, token, stream) = Fixture();
        var request = new RetryRequest(1, 2, 3);
        var admission = store.Admit(token, request, 1);
        // Discard the first admission reply, reopen the provider, and resubmit.
        var reopened = new RetryStore(scope, stream);
        Equal(admission, reopened.Admit(token, request, 2));
        Equal(901L, admission.Expires);
        Passed("lost admission reply preserves the first deadline");

        var result = reopened.Execute(token, request, 3);
        // Discard the commit reply; result lookup and repeat must preserve one effect.
        Equal(result, store.Status(token, 4));
        Equal(result, store.Execute(token, request, 4));
        Equal((6, 2, 1L, 1L, 1L), store.Inspect());
        Passed("lost committed reply resolves without duplicate effects");
    }

    private void ConcurrentDuplicates()
    {
        var (store, token, stream) = Fixture();
        var other = new RetryStore(scope, stream);
        var request = new RetryRequest(1, 2, 4);
        var admissions = new[]
        {
            Task.Run(() => store.Admit(token, request, 1)),
            Task.Run(() => other.Admit(token, request, 1))
        };
        Task.WaitAll(admissions);
        Equal(admissions[0].Result, admissions[1].Result);
        var calls = new[]
        {
            Task.Run(() => store.Execute(token, request, 2)),
            Task.Run(() => other.Execute(token, request, 2))
        };
        Task.WaitAll(calls);
        Equal(calls[0].Result, calls[1].Result);
        Equal(8, store.Inspect().Total);
        Passed("concurrent identical admission and execution converge");

        var second = token with { Operation = Guid.NewGuid() };
        var conflicting = new[]
        {
            Task.Run(() => store.Admit(second, request, 3)),
            Task.Run(() => other.Admit(second, request with { Delta = 5 }, 3))
        };
        Task.WaitAll(conflicting);
        Equal(1, conflicting.Count(task => task.Result.State == "Admitted"));
        Equal(1, conflicting.Count(task => task.Result.State == "PayloadMismatch"));
        Equal("PayloadMismatch", store.Execute(token, request with { Delta = 10 }, 4).State);
        Equal(8, store.Inspect().Total);
        Passed("different payloads cannot reuse an operation token");
    }

    private void RollbackAndReopen()
    {
        var (store, token, stream) = Fixture();
        var request = new RetryRequest(1, 2, 6);
        store.Admit(token, request, 1);
        try
        {
            store.Execute(token, request, 2, afterFirstEffect: () => throw new InjectedFailureException());
            throw new InvalidOperationException("Expected an injected failure.");
        }
        catch (InjectedFailureException)
        {
            Equal((0, 0, 0L, 1L, 0L), store.Inspect());
        }

        var reopened = new RetryStore(scope, stream);
        Equal("Admitted", reopened.Status(token, 3).State);
        Equal(901L, reopened.Status(token, 3).Expires);
        Equal("Committed", reopened.Execute(token, request, 4).State);
        Equal((12, 2, 1L, 1L, 1L), reopened.Inspect());
        Passed("batch midpoint rollback and same-history reopen preserve admission");
    }

    private void ExpiryAndConfiguration()
    {
        var (store, token, _) = Fixture();
        var request = new RetryRequest(1, 0, 1);
        store.Admit(token, request, 1);
        var epoch = store.CreateEpoch(2, window: 30);
        Equal(901L, store.Admit(token, request, 5).Expires);
        var newer = token with { Epoch = epoch, Operation = Guid.NewGuid() };
        Equal(35L, store.Admit(newer, request, 5).Expires);
        Equal("AdmissionClosed", store.Admit(token with { Operation = Guid.NewGuid() }, request, 5).State);
        Equal("Committed", store.Execute(token, request, 900).State);
        Equal("Expired", store.Execute(token, request, 901).State);
        Equal("Expired", store.Execute(newer, request, 901).State);
        Equal(1, store.Inspect().Total);
        Passed("configuration is prospective; closed generations reject new admissions");
        Passed("expiry rejects pending and committed repeats without execution");

        // A backward clock cannot revive an already expired token.
        Equal("Expired", store.Status(token, 10).State);
        Equal(false, store.Retire(epoch, 902));
        Equal(true, store.Retire(token.Epoch, 902));
        Equal(true, store.Retire(epoch, 902));
        Equal(0L, store.Inspect().Receipts);
        Equal("Expired", store.Admit(token, request, 903).State);
        Equal("Expired", store.Execute(token, request, 903).State);
        Equal(1, store.Inspect().Total);
        Passed("clock floor and retired-generation boundary survive receipt deletion");
    }

    private void NoOpAndRejection()
    {
        var (store, token, _) = Fixture();
        var remove = new RetryRequest(1, 0, 0, Delete: true);
        store.Admit(token, remove, 1);
        var result = store.Execute(token, remove, 2);
        Equal("Unchanged", result.Result);
        var insertion = token with { Operation = Guid.NewGuid() };
        var add = new RetryRequest(1, 0, 8);
        store.Admit(insertion, add, 3);
        store.Execute(insertion, add, 4);
        Equal(result, store.Execute(token, remove, 5));
        Equal(8, store.Inspect().Total);
        Passed("repeated absent Delete does not remove a later insertion");

        var rejected = token with { Operation = Guid.NewGuid() };
        store.Admit(rejected, add, 6);
        Equal("Rejected", store.Reject(rejected, add, 7).State);
        Equal("Rejected", store.Execute(rejected, add, 8).State);
        Equal(2L, store.Inspect().Journal);
        Equal(8, store.Inspect().Total);
        Passed("definitive rejection remains terminal on repeat");
    }

    private void CleanupRaces()
    {
        var (store, token, stream) = Fixture();
        var request = new RetryRequest(1, 2, 2);
        store.Admit(token, request, 1);
        Equal(false, store.Retire(token.Epoch, 900));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = Task.Run(() => store.Execute(token, request, 900, beforeCommit: () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("Controlled commit boundary timed out.");
            }
        }));
        Task<bool>? cleanup = null;
        try
        {
            Equal(true, entered.Wait(TimeSpan.FromSeconds(2)));
            cleanup = Task.Run(() => new RetryStore(scope, stream).Retire(token.Epoch, 902));
            var deadline = Stopwatch.StartNew();
            while (!scope.HasLockWaiter())
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(1))
                {
                    throw new TimeoutException("Expected a real PostgreSQL lock waiter.");
                }

                Thread.Sleep(10);
            }

            Equal(false, cleanup.IsCompleted);
        }
        finally
        {
            release.Set();
            writer.GetAwaiter().GetResult();
            cleanup?.GetAwaiter().GetResult();
        }

        Equal(true, cleanup!.Result);
        Equal((4, 2, 1L, 0L, 1L), store.Inspect());
        Equal("Expired", store.Execute(token, request, 903).State);
        Passed("retirement waits for a transaction started before expiry");

        var (rollbackStore, rollbackToken, _) = Fixture();
        rollbackStore.Admit(rollbackToken, request, 1);
        rollbackStore.Execute(rollbackToken, request, 2);
        try
        {
            rollbackStore.Retire(1, 902, () => throw new InjectedFailureException());
            throw new InvalidOperationException("Expected retirement rollback.");
        }
        catch (InjectedFailureException)
        {
            Equal(1L, rollbackStore.Inspect().Receipts);
            Equal("Committed", rollbackStore.Status(rollbackToken, 3).State);
        }

        Equal(true, rollbackStore.Retire(1, 902));
        Equal("Expired", rollbackStore.Status(rollbackToken, 3).State);
        Passed("retirement watermark and receipt removal commit atomically");
    }

    private void HistoryAndUnknown()
    {
        var (store, token, _) = Fixture();
        var request = new RetryRequest(1, 0, 1);
        Equal("Unknown", store.Status(token, 1).State);
        Equal("Unknown", store.Execute(token, request, 1).State);
        Equal(0, store.Inspect().Total);
        store.Admit(token, request, 2);
        store.Execute(token, request, 3);
        store.RestoreHistory(Guid.NewGuid());
        Equal("HistoryMismatch", store.Status(token, 4).State);
        Equal("HistoryMismatch", store.Admit(token, request, 4).State);
        Equal(1, store.Inspect().Total);
        Passed("missing receipts remain Unknown; restored history rejects old tokens");
    }

    private object MeasureSmallWrites()
    {
        var (store, token, _) = Fixture();
        var admissions = new List<double>();
        var completions = new List<double>();
        var before = GC.GetTotalAllocatedBytes(true);
        for (var i = 0; i < 14; i++)
        {
            var next = token with { Operation = Guid.NewGuid() };
            var request = new RetryRequest(1, 0, 1);
            var watch = Stopwatch.StartNew();
            store.Admit(next, request, 1);
            var admission = watch.Elapsed.TotalMilliseconds;
            store.Execute(next, request, 2);
            if (i >= 2)
            {
                admissions.Add(admission);
                completions.Add(watch.Elapsed.TotalMilliseconds);
            }

            Thread.Sleep(15);
        }

        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        admissions.Sort();
        completions.Sort();
        Equal(14, store.Inspect().Total);
        return new
        {
            Count = completions.Count,
            AdmissionMedianMs = admissions[admissions.Count / 2],
            AdmissionMaxMs = admissions[^1],
            CompleteMedianMs = completions[completions.Count / 2],
            CompleteMaxMs = completions[^1],
            ManagedAllocatedBytesIncludingWarmup = allocated,
            ManagedRetainedBytes = GC.GetTotalMemory(true)
        };
    }
}
