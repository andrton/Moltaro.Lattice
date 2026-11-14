namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal static class DirectedScenarios
{
    public static List<ScenarioResult> Run()
    {
        var results = new List<ScenarioResult>();
        RunCase(results, "Unchanged initial state", context => context.Finish());
        RunCase(results, "Update and append before copy", context =>
        {
            context.Write(Mutation.Set(ScenarioContext.A, 101));
            context.Write(Mutation.Add(ScenarioContext.A, 7));
            context.Finish();
            Check.That(context.Replica.SkippedEffects == 2, "Already captured effects were not skipped.");
        });
        RunCase(results, "Update and append after copy", context =>
        {
            context.CopyNext();
            context.Write(Mutation.Set(ScenarioContext.A, 102), Mutation.Add(ScenarioContext.B, 8));
            context.Write(Mutation.Add(ScenarioContext.A, 9));
            context.Finish();
        });
        RunCase(results, "Delete before copy after a partial change", context =>
        {
            context.Write(Mutation.Add(ScenarioContext.A, 11));
            context.Write(Mutation.Remove(ScenarioContext.A));
            Check.That(context.Master.RetainedDeletionCount == 1, "Missing fenced deletion metadata.");
            context.Finish();
        });
        RunCase(results, "Delete after copy", context =>
        {
            context.CopyNext();
            context.Write(Mutation.Remove(ScenarioContext.A));
            context.Finish();
        });
        RunCase(results, "Deleted fenced slot followed by a different key", context =>
        {
            context.Write(Mutation.Add(ScenarioContext.A, 12));
            context.Write(Mutation.Remove(ScenarioContext.A));
            context.Write(Mutation.Put(ScenarioContext.C, 13, 14));
            context.Finish();
        });
        RunCase(results, "Same-key recreation before copy", context =>
        {
            context.Write(Mutation.Remove(ScenarioContext.A));
            context.Write(Mutation.Put(ScenarioContext.A, 201, 21));
            context.Write(Mutation.Add(ScenarioContext.A, 22));
            context.Finish();
        });
        RunCase(results, "Same-key recreation after copy", context =>
        {
            context.CopyNext();
            context.Write(Mutation.Remove(ScenarioContext.A));
            context.Write(Mutation.Put(ScenarioContext.A, 202, 23));
            context.Write(Mutation.Set(ScenarioContext.A, 203));
            context.Finish();
        });
        RunCase(results, "New root beyond capture fence", context =>
        {
            context.Write(Mutation.Put(ScenarioContext.C, 301, 31));
            context.Write(Mutation.Add(ScenarioContext.C, 32));
            context.Finish();
        });
        RunCase(results, "WriteBatch between two root copies", context =>
        {
            context.CopyNext();
            context.Write(Mutation.Put(ScenarioContext.A, 401, 41), Mutation.Put(ScenarioContext.B, 402, 42));
            Check.Rejects(() => context.Replica.Observe(), "Mixed bootstrap state became readable.");
            context.Finish();
            Check.That(context.Replica.SkippedEffects == 1, "Batch must filter members by their own base revision.");
        });
        RunCase(results, "Batch deletion and insertion across types", context =>
        {
            context.CopyNext();
            context.Write(Mutation.Remove(ScenarioContext.A), Mutation.Put(ScenarioContext.C, 501, 51),
                Mutation.Put(ScenarioContext.B, 502, 52));
            context.Finish();
        });
        RunCase(results, "Publication after fixed readiness boundary", BoundaryThenLive);
        RunCase(results, "Duplicate transaction is not applied twice", DuplicateTransaction);
        RunCase(results, "Incomplete initial batch is never ready", IncompleteInitialBatch);
        RunCase(results, "Missing tail range requires reload", MissingRange);
        RunCase(results, "Missing base frame is rejected", context =>
        {
            context.CopyNext();
            Check.Rejects(() => context.Replica.CompleteBase(context.Master.PublishedPosition),
                "Missing base frame was accepted.");
            Check.That(context.Replica.State == ReplicaStateEnum.ReloadRequired, "Failure did not close readiness.");
        });
        RunCase(results, "Invalid prepared batch leaves master state unchanged", context =>
        {
            var before = context.Master.PublishedPosition;
            Check.Rejects(() => context.Master.Publish(Mutation.Set(ScenarioContext.A, 999), Mutation.Add(ScenarioContext.C, 1)),
                "Invalid batch unexpectedly committed.");
            Check.That(context.Master.PublishedPosition == before, "Invalid batch advanced master progress.");
            context.Finish();
        });
        RunCase(results, "Live batch waits for complete frame", LiveBatch);
        RunRetentionLimit(results);
        RunEmptySlotFence(results);
        return results;
    }

    private static void RunCase(List<ScenarioResult> results, string name, Action<ScenarioContext> action)
    {
        using (var context = new ScenarioContext())
        {
            context.Write(Mutation.Put(ScenarioContext.A, 10, 1), Mutation.Put(ScenarioContext.B, 20, 2));
            Check.That(context.Master.PendingCount == 0, "History accumulated without a recipient.");
            context.Open();
            action(context);
        }
        Check.That(NativeBlock.LiveBlocks == 0, "Native blocks leaked after a scenario.");
        results.Add(new ScenarioResult(name, "Passed"));
    }

    private static void BoundaryThenLive(ScenarioContext context)
    {
        context.Write(Mutation.Add(ScenarioContext.A, 61));
        context.CompleteBase();
        var boundaryValues = context.Reference.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        var live = context.Write(Mutation.Set(ScenarioContext.A, 602));
        foreach (var transaction in context.Master.Pending().Where(item => item.Position <= context.Boundary))
        {
            ScenarioContext.Deliver(context.Replica, transaction);
        }
        context.Replica.MarkReady();
        Check.That(Check.Same(boundaryValues, context.Replica.Observe()), "Readiness did not match its fixed boundary.");
        context.Master.Acknowledge(context.Replica.AppliedPosition!.Value);
        Check.That(context.Master.PendingCount == 1, "Post-boundary update was trimmed or lost.");
        ScenarioContext.Deliver(context.Replica, live);
        context.Master.Acknowledge(context.Replica.AppliedPosition!.Value);
        context.Verify();
        Check.That(context.Master.PendingCount == 0, "Live acknowledgment did not release delivery.");
    }

    private static void DuplicateTransaction(ScenarioContext context)
    {
        context.CopyNext();
        var transaction = context.Write(Mutation.Add(ScenarioContext.A, 71));
        context.CompleteBase();
        ScenarioContext.Deliver(context.Replica, transaction);
        ScenarioContext.Deliver(context.Replica, transaction);
        Check.That(context.Replica.AppliedPosition is null, "Mixed initial state gained an applied position.");
        context.Replica.MarkReady();
        ScenarioContext.Deliver(context.Replica, transaction);
        context.Master.Acknowledge(context.Replica.AppliedPosition!.Value);
        context.Verify();
    }

    private static void IncompleteInitialBatch(ScenarioContext context)
    {
        var transaction = context.Write(Mutation.Put(ScenarioContext.A, 801), Mutation.Put(ScenarioContext.B, 802));
        context.CompleteBase();
        context.Replica.BeginTransaction(transaction.Position, transaction.Effects.Length);
        context.Replica.ReceiveEffect(transaction.Effects[0]);
        Check.Rejects(() => context.Replica.Observe(), "Partial initial batch became readable.");
        Check.That(context.Replica.AppliedPosition is null, "Partial batch was acknowledged.");
        Check.Rejects(context.Replica.MarkReady, "Incomplete batch reached Ready.");
        Check.That(context.Master.PendingCount == 1, "Unacknowledged batch was released.");
    }

    private static void MissingRange(ScenarioContext context)
    {
        context.CopyNext();
        context.Write(Mutation.Add(ScenarioContext.A, 91));
        var second = context.Write(Mutation.Set(ScenarioContext.A, 902));
        context.CompleteBase();
        Check.Rejects(() => ScenarioContext.Deliver(context.Replica, second), "A gap was silently skipped.");
        Check.That(context.Replica.AppliedPosition is null && context.Replica.State == ReplicaStateEnum.ReloadRequired,
            "A failed catch-up remained usable.");
    }

    private static void LiveBatch(ScenarioContext context)
    {
        context.Finish();
        var before = context.Replica.Observe();
        var beforePosition = context.Replica.AppliedPosition;
        var transaction = context.Write(Mutation.Put(ScenarioContext.A, 1001, 101), Mutation.Put(ScenarioContext.B, 1002, 102));
        context.Replica.BeginTransaction(transaction.Position, transaction.Effects.Length);
        context.Replica.ReceiveEffect(transaction.Effects[0]);
        Check.That(Check.Same(before, context.Replica.Observe()) && context.Replica.AppliedPosition == beforePosition,
            "A partial live frame changed visible state or progress.");
        context.Replica.ReceiveEffect(transaction.Effects[1]);
        context.Replica.CommitFrame();
        context.Master.Acknowledge(context.Replica.AppliedPosition!.Value);
        context.Verify();
    }

    private static void RunRetentionLimit(List<ScenarioResult> results)
    {
        using (var context = new ScenarioContext(maximumPending: 2))
        {
            context.Write(Mutation.Put(ScenarioContext.A, 1));
            context.Open();
            context.Write(Mutation.Set(ScenarioContext.A, 2));
            context.Write(Mutation.Remove(ScenarioContext.A));
            context.Write(Mutation.Put(ScenarioContext.A, 3));
            Check.Rejects(() => context.Master.CaptureNext(), "Expired capture remained active.");
            Check.Rejects(() => context.Master.Pending(), "Missing RAM tail had an unexpected fallback.");
            Check.That(context.Master.PendingCount == 0 && context.Master.RetainedDeletionCount == 0,
                "Expired capture retained delivery or tombstones.");
            Check.Rejects(() => context.Replica.Observe(), "Expired initial replica became readable.");
        }
        Check.That(NativeBlock.LiveBlocks == 0, "Native leak after capture expiration.");
        results.Add(new ScenarioResult("Bounded retention expiration invalidates capture", "Passed"));
    }

    private static void RunEmptySlotFence(List<ScenarioResult> results)
    {
        using (var context = new ScenarioContext())
        {
            context.Write(Mutation.Put(ScenarioContext.A, 1), Mutation.Put(ScenarioContext.B, 2));
            context.Write(Mutation.Remove(ScenarioContext.A));
            context.Open();
            context.Write(Mutation.Put(ScenarioContext.C, 3));
            context.Finish();
        }
        Check.That(NativeBlock.LiveBlocks == 0, "Native leak after empty-slot capture.");
        results.Add(new ScenarioResult("Empty fenced slot is not reused during capture", "Passed"));
    }
}
