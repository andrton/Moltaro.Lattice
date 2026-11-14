using System.Diagnostics;
using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal static class UnitCrashChecks
{
    private static string Stream(UnitCrashPhaseEnum phase) => "units_crash_" + phase;

    public static void Crash(PgRunScope scope, UnitCrashPhaseEnum phase)
    {
        using var store = new PgBatchStore(scope, Stream(phase), SavedBaseKindEnum.Units256);
        using var maintenance = new PgBatchStore(scope, Stream(phase), SavedBaseKindEnum.Units256);
        store.Initialize(BatchFixture.Initial());
        using var engine = new BatchEngine([], store);
        engine.Write(UnitFixture.Token(1), UnitFixture.Changes(1));
        if (phase <= UnitCrashPhaseEnum.ForegroundAfterPublication)
        {
            maintenance.Units!.ApplyOnce();
            maintenance.Units.Cleanup();
            engine.Write(UnitFixture.Token(2), UnitFixture.Changes(2), (point, member) =>
            {
                var wanted = phase switch
                {
                    UnitCrashPhaseEnum.ForegroundBeforeCommit => BatchPhaseEnum.BeforeCommit,
                    UnitCrashPhaseEnum.ForegroundAfterCommit => BatchPhaseEnum.AfterCommit,
                    UnitCrashPhaseEnum.ForegroundMemberPublished => BatchPhaseEnum.MemberPublished,
                    _ => BatchPhaseEnum.AfterPublication
                };
                if (point == wanted && (point != BatchPhaseEnum.MemberPublished || member == 1))
                {
                    Exit(phase);
                }
            });
        }
        else
        {
            engine.Write(UnitFixture.Token(2), UnitFixture.Changes(2));
            maintenance.Units!.ApplyOnce(point =>
            {
                if (point.ToString() == phase.ToString())
                {
                    Exit(phase);
                }
            });
            // Cleanup must not remove the next committed, unapplied transaction.
            engine.Write(UnitFixture.Token(3), UnitFixture.Changes(3));
            maintenance.Units.Cleanup(point =>
            {
                if (point.ToString() == phase.ToString())
                {
                    Exit(phase);
                }
            });
        }
        throw new InvalidOperationException("Unit crash hook did not fire.");
    }

    private static void Exit(UnitCrashPhaseEnum phase) => Environment.Exit(110 + (int)phase);

    public static object Recover(PgRunScope scope, UnitCrashPhaseEnum phase)
    {
        var foreground = phase <= UnitCrashPhaseEnum.ForegroundAfterPublication;
        var cleanup = phase is UnitCrashPhaseEnum.CleanupDeleted or UnitCrashPhaseEnum.CleanupCommitted;
        var committed = phase == UnitCrashPhaseEnum.ForegroundBeforeCommit ? 1 : cleanup ? 3 : 2;
        var basePosition = foreground ? 1 : phase is UnitCrashPhaseEnum.UnitsWritten or UnitCrashPhaseEnum.CursorWritten ? 0 : 2;
        using var store = new PgBatchStore(scope, Stream(phase), SavedBaseKindEnum.Units256);
        var before = store.ReadRecovery();
        UnitFixture.VerifyRecoveryState(before, committed);
        Check.That(before.Position == basePosition, "Crash left an incorrect applied cursor.");
        var expectedRows = foreground ? committed - 1 : phase == UnitCrashPhaseEnum.CleanupCommitted ? 1 : committed;
        Check.That(store.RowCount() == expectedRows, "Crash cleanup lost or retained an unexpected journal boundary.");
        var timer = Stopwatch.StartNew();
        double recovered;
        ReadSummary?[] captured;
        using (var engine = new BatchEngine([], store))
        {
            recovered = timer.Elapsed.TotalMilliseconds;
            engine.Audit(UnitFixture.Expected(committed));
            captured = UnitFixture.Capture(engine);
            Check.That(engine.Write(UnitFixture.Token(1), UnitFixture.Changes(1)).ReplayedReceipt, "Old cleaned receipt was lost.");
            UnitFixture.VerifyCaptured(engine, captured);
            if (committed == 1)
            {
                Check.That(!engine.Write(UnitFixture.Token(2), UnitFixture.Changes(2)).ReplayedReceipt,
                    "Rolled-back foreground operation retained a receipt.");
                committed = 2;
            }
            else
            {
                Check.That(engine.Write(UnitFixture.Token(committed), UnitFixture.Changes(committed)).ReplayedReceipt, "Committed receipt was lost.");
            }
            captured = UnitFixture.Capture(engine);
        }

        // Also prove bounded maintenance can resume after every failure, remove
        // covered rows and retain exactly the same values, indexes and revisions.
        while (store.Units!.ApplyOnce().Batches > 0) { }
        store.Units.Cleanup();
        using (var again = new BatchEngine([], store))
        {
            again.Audit(UnitFixture.Expected(committed));
            UnitFixture.VerifyCaptured(again, captured);
            Check.That(again.Position == committed && store.RowCount() == 0, "Resumed maintenance did not converge.");
        }
        return new { Phase = phase.ToString(), BasePositionAfterCrash = basePosition,
            CommittedPositionAfterRetry = committed, JournalRowsBeforeRecovery = expectedRows,
            RecoveryMilliseconds = recovered, Checks = "Whole-batch restore, receipts, resumed apply/cleanup and exact revisions passed" };
    }
}
