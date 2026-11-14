using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal static class BatchChecks
{
    public static string[] Run(PgRunScope? scope, SavedBaseKindEnum kind = SavedBaseKindEnum.FixtureImage)
    {
        var initial = BatchFixture.Initial();
        using var store = scope is null ? null : new PgBatchStore(scope, "checks_" + kind, kind);
        store?.Initialize(initial);
        using var engine = new BatchEngine(initial, store);
        var oracle = BatchFixture.Oracle(initial);
        var passed = new List<string>();

        var duplicate = BatchFixture.Changes(2, 1);
        duplicate[1] = duplicate[0];
        BatchFixture.ExpectFailure(() => engine.Write(Guid.NewGuid(), duplicate), "Duplicate type/key rejects the whole batch.");
        var invalid = BatchFixture.Changes(8, 1);
        invalid[^1] = invalid[^1] with { Value = invalid[^1].Value with { Length = 4096 } };
        BatchFixture.ExpectFailure(() => engine.Write(Guid.NewGuid(), invalid), "Invalid replacement.");
        engine.Audit(oracle);
        passed.Add("Whole-batch duplicate and invalid-value rejection");

        try
        {
            engine.Write(Guid.NewGuid(), BatchFixture.Changes(8, 1), (phase, member) =>
            {
                if (phase == BatchPhaseEnum.MemberPrepared && member == 2)
                {
                    throw new OutOfMemoryException("Injected preparation allocation failure.");
                }
            });
            throw new InvalidOperationException("Injected preparation failure was not observed.");
        }
        catch (OutOfMemoryException)
        {
            engine.Audit(oracle);
            Check.That(engine.Snapshot().PreparedBlocks == 0, "Aborted batch retained private blocks.");
        }

        using (var limited = new BatchEngine(initial, preparationLimit: 1024))
        {
            BatchFixture.ExpectFailure(() => limited.Write(Guid.NewGuid(), BatchFixture.Changes(8, 2)),
                "Native preparation budget rejected before commit.");
            limited.Audit(BatchFixture.Oracle(initial));
        }
        passed.Add("Preparation refusal and partial preparation cleanup");

        var rejectedToken = Guid.NewGuid();
        try
        {
            engine.Write(rejectedToken, BatchFixture.Changes(8, 2), (phase, _) =>
            {
                if (phase == BatchPhaseEnum.BeforeCommit)
                {
                    throw new InjectedFailureException();
                }
            });
            throw new InvalidOperationException("Rollback injection did not fire.");
        }
        catch (Exception exception) when (exception is InjectedFailureException or CommitFailureException { Uncertain: false })
        {
            Check.That(!engine.Faulted && engine.Position == 0 && store?.Find(rejectedToken) is null,
                "Pre-commit failure changed state or retained a receipt.");
            engine.Audit(oracle);
        }
        passed.Add("Rollback before commit leaves data, indexes and receipt unchanged");

        var changes = BatchFixture.Changes(8, 3);
        HoldBeforeCommit(engine, changes);
        BatchFixture.Apply(oracle, changes);
        engine.Audit(oracle);
        passed.Add("Readers and compaction continue during prepared SQL wait");

        changes = [BatchMutation.Save(new EntityKey(EntityTypeEnum.User, 0), 4),
            BatchMutation.Save(new EntityKey(EntityTypeEnum.Role, 0), 4),
            BatchMutation.Delete(new EntityKey(EntityTypeEnum.User, 1)),
            BatchMutation.Save(new EntityKey(EntityTypeEnum.Role, 200), 4)];
        HoldDuringPublication(engine, changes);
        BatchFixture.Apply(oracle, changes);
        engine.Audit(oracle);
        passed.Add("Multi-type Upsert/Delete publication excludes reads and index queries");

        changes = BatchFixture.Changes(8, 5);
        DrainExistingReader(engine, changes);
        BatchFixture.Apply(oracle, changes);
        engine.Audit(oracle);
        passed.Add("Publication drains an existing protected reader");

        var token = Guid.NewGuid();
        changes = BatchFixture.Changes(8, 6);
        var original = engine.Write(token, changes);
        var revision = engine.Read(changes[0].Identity)!.Value.Revision;
        var repeat = engine.Write(token, changes);
        Check.That(repeat.ReplayedReceipt && repeat.Receipt == original.Receipt &&
            engine.Read(changes[0].Identity)!.Value.Revision == revision, "Same-token repeat applied twice.");
        BatchFixture.ExpectFailure(() => engine.Write(token, BatchFixture.Changes(8, 7)), "Operation token reused with different input.");
        BatchFixture.Apply(oracle, changes);
        engine.Audit(oracle);
        Check.That(store is null || store.RowCount() == 4, "Unexpected committed journal row count.");
        passed.Add("Stable receipt and no second publication on safe retry");

        var first = BatchFixture.Changes(8, 8);
        var second = BatchFixture.Changes(8, 9);
        QueueConflictingCallers(engine, first, second);
        BatchFixture.Apply(oracle, second);
        engine.Audit(oracle);
        Check.That(engine.Position == 6 && (store is null || store.RowCount() == 6), "Concurrent caller order mismatch.");
        passed.Add("Concurrent callers on the same roots preserve commit/publication order");
        return passed.ToArray();
    }

    private static void HoldBeforeCommit(BatchEngine engine, BatchMutation[] changes)
    {
        var old = engine.Read(changes[0].Identity);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var writer = new TestThread(() => engine.Write(Guid.NewGuid(), changes, (phase, _) =>
        {
            if (phase == BatchPhaseEnum.BeforeCommit)
            {
                entered.Set();
                Check.That(release.Wait(TimeSpan.FromSeconds(3)), "Storage wait test timed out.");
            }
        }));
        try
        {
            Check.That(entered.Wait(TimeSpan.FromSeconds(2)), "Storage boundary was not reached.");
            Check.That(engine.Read(changes[0].Identity) == old, "Uncommitted state became visible.");
            Check.That(engine.Snapshot().PreparedBlocks > 0, "Missing prepared batch memory.");
            for (var step = 0; step < 16; step++)
            {
                engine.CompactStep();
            }
            Check.That(engine.Read(changes[0].Identity) == old, "Prepared compaction changed old data.");
        }
        finally
        {
            release.Set();
            writer.Dispose();
        }
    }

    private static void HoldDuringPublication(BatchEngine engine, BatchMutation[] changes)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var oldPosition = engine.Position;
        var read = (ReadSummary?)null;
        var queryRows = -1;
        var scratch = new RootClaim[ArenaModel.KeyLimit];
        var results = new ReadSummary[ArenaModel.KeyLimit];
        using var writer = new TestThread(() => engine.Write(Guid.NewGuid(), changes, (phase, member) =>
        {
            if (phase == BatchPhaseEnum.MemberPublished && member == 1)
            {
                entered.Set();
                Check.That(release.Wait(TimeSpan.FromSeconds(3)), "Partial publication test timed out.");
            }
        }));
        TestThread? reader = null;
        TestThread? query = null;
        try
        {
            Check.That(entered.Wait(TimeSpan.FromSeconds(2)), "Publication midpoint not reached.");
            reader = new TestThread(() => read = engine.Read(changes[0].Identity));
            query = new TestThread(() => queryRows = engine.Query(TextTable.Expected(changes[0].Value.TextGroup), scratch, results));
            TestThread.Await(() => engine.WaitingReaders == 2, "Reader/query escaped partial publication.");
            Check.That(engine.Position == oldPosition, "Applied position advanced over a prefix.");
            Check.That(!engine.CompactStep(), "Compactor entered partial batch publication.");
        }
        finally
        {
            release.Set();
            writer.Dispose();
            reader?.Dispose();
            query?.Dispose();
        }

        Check.That(read?.Number == changes[0].Value.Number && queryRows >= 2,
            "Readers did not observe complete publication after release.");
    }

    private static void DrainExistingReader(BatchEngine engine, BatchMutation[] changes)
    {
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var reader = new TestThread(() => engine.HeldReadForTest(() =>
        {
            held.Set();
            Check.That(release.Wait(TimeSpan.FromSeconds(3)), "Held batch reader timed out.");
        }));
        TestThread? writer = null;
        try
        {
            Check.That(held.Wait(TimeSpan.FromSeconds(2)), "Protected read did not enter.");
            writer = new TestThread(() => engine.Write(Guid.NewGuid(), changes));
            TestThread.Await(() => engine.WaitingPublishers > 0, "Batch did not wait for existing protected access.");
        }
        finally
        {
            release.Set();
            writer?.Dispose();
            reader.Dispose();
        }
    }

    private static void QueueConflictingCallers(BatchEngine engine, BatchMutation[] first, BatchMutation[] second)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var submitted = new ManualResetEventSlim();
        BatchObservation one = default, two = default;
        using var writer = new TestThread(() => one = engine.Write(Guid.NewGuid(), first, (phase, _) =>
        {
            if (phase == BatchPhaseEnum.BeforeCommit)
            {
                entered.Set();
                Check.That(release.Wait(TimeSpan.FromSeconds(3)), "Ordered caller test timed out.");
            }
        }));
        TestThread? follower = null;
        try
        {
            Check.That(entered.Wait(TimeSpan.FromSeconds(2)), "First caller did not reach commit boundary.");
            follower = new TestThread(() =>
            {
                submitted.Set();
                two = engine.Write(Guid.NewGuid(), second);
            });
            Check.That(submitted.Wait(TimeSpan.FromSeconds(2)), "Second caller did not submit.");
        }
        finally
        {
            release.Set();
            writer.Dispose();
            follower?.Dispose();
        }
        Check.That(two.Receipt.Position == one.Receipt.Position + 1, "Conflicting writes overtook each other.");
    }
}
