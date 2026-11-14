using System.Diagnostics;
using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal static class FailureChecks
{
    public static readonly BatchPhaseEnum[] CrashPhases =
        [BatchPhaseEnum.BeforeCommit, BatchPhaseEnum.AfterCommit, BatchPhaseEnum.MemberPublished, BatchPhaseEnum.AfterPublication];

    public static string[] Run(PgRunScope? scope, SavedBaseKindEnum kind = SavedBaseKindEnum.FixtureImage)
    {
        var checks = new List<string>();
        foreach (var failure in CrashPhases.Skip(1))
        {
            var initial = BatchFixture.Initial();
            var changes = BatchFixture.Changes(8, 11);
            var oracle = BatchFixture.Oracle(initial);
            var token = Guid.NewGuid();
            using var store = scope is null ? null : new PgBatchStore(scope, "fault_" + kind + failure, kind);
            store?.Initialize(initial);
            using (var engine = new BatchEngine(initial, store))
            {
                TestThread? waitingReader = null;
                try
                {
                    engine.Write(token, changes, (phase, member) =>
                    {
                        if (phase == failure && (phase != BatchPhaseEnum.MemberPublished || member == 1))
                        {
                            if (phase == BatchPhaseEnum.MemberPublished)
                            {
                                waitingReader = new TestThread(() => BatchFixture.ExpectFailure(
                                    () => engine.Read(changes[0].Identity), "Lattice instance requires reopen."));
                                TestThread.Await(() => engine.WaitingReaders == 1, "Fault observer did not wait for publication.");
                            }
                            throw new InjectedFailureException();
                        }
                    });
                    throw new InvalidOperationException("Publication fault injection did not fire.");
                }
                catch (InjectedFailureException)
                {
                    Check.That(engine.Faulted, "Committed failure did not stop the instance.");
                    BatchFixture.ExpectFailure(() => engine.Read(changes[0].Identity), "Lattice instance requires reopen.");
                    BatchFixture.ExpectFailure(() => engine.Write(Guid.NewGuid(), changes), "Lattice instance requires reopen.");
                    BatchFixture.ExpectFailure(() => engine.CompactStep(), "Lattice instance requires reopen.");
                }
                finally
                {
                    waitingReader?.Dispose();
                }
            }

            Check.That(NativeSegment.OutstandingBytes == 0, "Failed arena was not closed before reopen.");
            if (store is not null)
            {
                using var restored = new BatchEngine([], store);
                BatchFixture.Apply(oracle, changes);
                restored.Audit(oracle);
                var revision = restored.Read(changes[0].Identity)!.Value.Revision;
                var repeat = restored.Write(token, changes);
                Check.That(repeat.ReplayedReceipt && repeat.Receipt == new BatchReceipt(1, 8) &&
                    restored.Position == 1 && store.RowCount() == 1 && restored.Read(changes[0].Identity)!.Value.Revision == revision,
                    "Recovery or retained result applied a committed batch twice.");
            }

            checks.Add(scope is null ? $"Fault closes access at {failure}" : $"Single-arena recovery and safe retry after {failure}");
        }

        return checks.ToArray();
    }

    public static string Stream(BatchPhaseEnum phase) => "crash_" + phase;
    public static Guid Token(BatchPhaseEnum phase) => new(100 + (int)phase, 0, 0, new byte[8]);

    public static void Crash(PgRunScope scope, BatchPhaseEnum phase)
    {
        using var store = new PgBatchStore(scope, Stream(phase));
        using var engine = new BatchEngine([], store);
        engine.Write(Token(phase), BatchFixture.Changes(8, 17), (current, member) =>
        {
            if (current == phase && (current != BatchPhaseEnum.MemberPublished || member == 1))
            {
                // Abrupt application exit: no finally/disposal/recovery in this worker.
                // The PostgreSQL server and all unrelated processes remain running.
                Environment.Exit(90 + (int)phase);
            }
        });
        throw new InvalidOperationException("Crash boundary was not reached.");
    }

    public static object Recover(PgRunScope scope, BatchPhaseEnum phase)
    {
        var oracle = BatchFixture.Oracle(BatchFixture.Initial());
        var changes = BatchFixture.Changes(8, 17);
        var committed = phase != BatchPhaseEnum.BeforeCommit;
        using var store = new PgBatchStore(scope, Stream(phase));
        var started = Stopwatch.GetTimestamp();
        using var engine = new BatchEngine([], store);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (committed)
        {
            BatchFixture.Apply(oracle, changes);
        }

        engine.Audit(oracle);
        Check.That(engine.Position == (committed ? 1 : 0) && store.RowCount() == engine.Position,
            "Crash left an incomplete persisted batch.");
        var before = engine.Read(changes[0].Identity)!.Value.Revision;
        var repeated = engine.Write(Token(phase), changes);
        Check.That(repeated.ReplayedReceipt == committed && repeated.Receipt == new BatchReceipt(1, 8) && store.RowCount() == 1,
            "Crash retry produced a duplicate or missing transaction.");
        if (committed)
        {
            Check.That(engine.Read(changes[0].Identity)!.Value.Revision == before, "Retry changed the recovered revision.");
        }
        BatchFixture.Apply(oracle, changes);
        engine.Audit(oracle);
        return new { Phase = phase.ToString(), CommittedBeforeCrash = committed, RecoveryMilliseconds = elapsed,
            RestoredRoots = 256, RetriedReceipt = repeated.ReplayedReceipt, Native = engine.Snapshot(), Checks = "Whole-batch recovery, indexes, receipt and retry passed" };
    }
}
