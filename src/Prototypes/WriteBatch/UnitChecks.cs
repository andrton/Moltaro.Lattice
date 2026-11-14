using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal static class UnitChecks
{
    public static string[] Run(PgRunScope scope)
    {
        var passed = new List<string>();
        const string stream = "unit_interleavings";
        using var store = new PgBatchStore(scope, stream, SavedBaseKindEnum.Units256);
        using var maintenance = new PgBatchStore(scope, stream, SavedBaseKindEnum.Units256);
        using var observer = new PgBatchStore(scope, stream, SavedBaseKindEnum.Units256);
        store.Initialize(BatchFixture.Initial());
        var units = maintenance.Units!;
        ReadSummary?[] committed;
        using (var engine = new BatchEngine([], store))
        {
            engine.Write(UnitFixture.Token(1), UnitFixture.Changes(1));
            engine.Write(UnitFixture.Token(2), UnitFixture.Changes(2));
            foreach (var failure in new[] { UnitApplyPhaseEnum.UnitsWritten, UnitApplyPhaseEnum.CursorWritten })
            {
                try
                {
                    units.ApplyOnce(phase =>
                    {
                        if (phase == failure)
                        {
                            var old = observer.ReadRecovery();
                            Check.That(old.Position == 0 && old.Roots.SequenceEqual(RecoveryState.Initial(BatchFixture.Initial()).Roots.OrderBy(root => root.Mutation.Identity.Slot)),
                                "Uncommitted units or cursor escaped the transaction.");
                            UnitFixture.VerifyRecoveryState(old, 2);
                            throw new InjectedFailureException();
                        }
                    });
                    throw new InvalidOperationException("Applier rollback hook was not reached.");
                }
                catch (InjectedFailureException)
                {
                    Check.That(units.Applied == 0 && store.RowCount() == 2 && units.Cleanup() == 0,
                        "Failed application allowed cleanup or advanced the cursor.");
                }
            }
            passed.Add("Unit changes and applied cursor roll back together; failed apply cannot permit cleanup");

            var sample = units.ApplyOnce(phase =>
            {
                if (phase == UnitApplyPhaseEnum.UnitsWritten)
                {
                    Check.That(engine.Read(new EntityKey(EntityTypeEnum.User, 0)) is not null, "Maintenance blocked arena read.");
                    engine.CompactStep();
                    engine.Write(UnitFixture.Token(3), UnitFixture.Changes(3));
                    Check.That(observer.Units!.Applied == 0, "Partial base became visible.");
                }
            });
            Check.That(sample.Batches == 2 && sample.Mutations == 16 && sample.RootsWritten == 8 && sample.Through == 2,
                "Coalescing did not preserve a complete bounded prefix.");
            Check.That(units.Cleanup() == 2 && store.RowCount() == 1, "Cleanup deleted an unapplied tail.");
            var before = UnitFixture.Capture(engine);
            Check.That(engine.Write(UnitFixture.Token(1), UnitFixture.Changes(1)).ReplayedReceipt, "Receipt disappeared with journal cleanup.");
            UnitFixture.VerifyCaptured(engine, before);
            passed.Add("Coalesced mixed changes preserve boundaries while foreground writes continue; tail and receipts survive cleanup");

            var snapshot = observer.ReadRecovery(() =>
            {
                Check.That(units.ApplyOnce().Through == 3 && units.Cleanup() == 1, "Concurrent maintenance failed.");
            });
            Check.That(snapshot.Position == 2 && snapshot.Tail.Length == 1, "Recovery snapshot changed after capture.");
            UnitFixture.VerifyRecoveryState(snapshot, 3);
            engine.Audit(UnitFixture.Expected(3));
            passed.Add("Repeatable-read recovery keeps the matching old base and tail during apply/cleanup");

            // Persisted maintenance is allowed to overtake local publication. A
            // post-commit fault must recover even when the journal is already gone.
            try
            {
                engine.Write(UnitFixture.Token(4), UnitFixture.Changes(4), (phase, _) =>
                {
                    if (phase == BatchPhaseEnum.AfterCommit)
                    {
                        Check.That(units.ApplyOnce().Through == 4 && units.Cleanup() == 1, "Pre-publication maintenance failed.");
                        throw new InjectedFailureException();
                    }
                });
                throw new InvalidOperationException("Committed fault was not injected.");
            }
            catch (InjectedFailureException)
            {
                Check.That(engine.Faulted && store.RowCount() == 0, "Faulted state/cleaned journal mismatch.");
            }
        }
        Check.That(NativeSegment.OutstandingBytes == 0, "Old arena remained during reopen.");
        using (var reopened = new BatchEngine([], store))
        {
            reopened.Audit(UnitFixture.Expected(4));
            Check.That(reopened.Position == 4 && reopened.Write(UnitFixture.Token(4), UnitFixture.Changes(4)).ReplayedReceipt,
                "Cleaned committed batch was not restored.");
            committed = UnitFixture.Capture(reopened);
            reopened.Write(UnitFixture.Token(5), UnitFixture.Changes(5));
            Check.That(reopened.Read(new EntityKey(EntityTypeEnum.User, 0))!.Value.Revision > committed.Max(value => value?.Revision ?? 0),
                "New write reused a stored revision.");
            committed = UnitFixture.Capture(reopened);
            units.ApplyOnce();
            units.Cleanup();
        }
        using (var restored = new BatchEngine([], store))
        {
            restored.Audit(UnitFixture.Expected(5));
            UnitFixture.VerifyCaptured(restored, committed);
        }
        passed.Add("Compacted base restores a committed unpublished batch, exact revisions and safe retries without a second arena");

        EmptyBase(scope);
        passed.Add("All roots deleted: empty base preserves position and revision floor through recreation");
        return passed.ToArray();
    }

    private static void EmptyBase(PgRunScope scope)
    {
        using var store = new PgBatchStore(scope, "unit_empty", SavedBaseKindEnum.Units256);
        store.Initialize(BatchFixture.Initial().Take(2).ToArray());
        using (var engine = new BatchEngine([], store))
        {
            engine.Write(Guid.NewGuid(), BatchFixture.Initial().Take(2).Select(value => BatchMutation.Delete(value.Identity)).ToArray());
            store.Units!.ApplyOnce();
            store.Units.Cleanup();
        }
        Check.That(store.ReadRecovery().Roots.Length == 0, "Empty base retained roots.");
        using var restored = new BatchEngine([], store);
        Check.That(restored.Position == 1, "Empty base lost applied position.");
        restored.Write(Guid.NewGuid(), BatchFixture.Changes(1, 9));
        Check.That(restored.Read(new EntityKey(EntityTypeEnum.User, 0))!.Value.Revision == 3, "Empty base lost revision floor.");
    }
}
