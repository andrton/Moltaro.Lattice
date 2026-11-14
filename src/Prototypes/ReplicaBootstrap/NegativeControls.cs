namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal static class NegativeControls
{
    public static List<ScenarioResult> Run()
    {
        var results = new List<ScenarioResult>();
        using (var context = new ScenarioContext())
        {
            context.Write(Mutation.Put(ScenarioContext.A, 1, 1));
            context.Open(ReplayRuleEnum.Unconditional);
            context.Write(Mutation.Add(ScenarioContext.A, 2));
            context.Finish(verify: false);
            Check.That(!Check.Same(context.Reference.Values, context.Replica.Observe()),
                "Blind replay unexpectedly matched the oracle.");
            Check.That(context.Replica.Observe()[ScenarioContext.A].Items.SequenceEqual(new[] { 1, 2, 2 }),
                "Expected duplicate append was not reproduced.");
        }
        results.Add(new ScenarioResult("Unconditional replay", "Expected counterexample: duplicate appended element"));

        using (var context = new ScenarioContext())
        {
            context.Write(Mutation.Put(ScenarioContext.A, 1, 1));
            context.Open(ReplayRuleEnum.IgnoreDeletionWatermarks);
            context.Write(Mutation.Add(ScenarioContext.A, 2));
            context.Write(Mutation.Remove(ScenarioContext.A));
            Check.Rejects(() => context.Finish(), "Missing deletion watermark did not expose the predecessor problem.");
            Check.That(context.Replica.State == ReplicaStateEnum.ReloadRequired,
                "Failed predecessor was not rejected.");
        }
        results.Add(new ScenarioResult("Live-root revisions without deletion watermarks",
            "Expected counterexample: partial effect has no baseline root"));

        using (var context = new ScenarioContext())
        {
            context.Write(Mutation.Put(ScenarioContext.A, 1), Mutation.Put(ScenarioContext.B, 2));
            context.Open(ReplayRuleEnum.MaximumBaseRevision);
            context.CopyNext();
            context.Write(Mutation.Put(ScenarioContext.A, 10), Mutation.Put(ScenarioContext.B, 20));
            context.Finish(verify: false);
            var result = context.Replica.Observe();
            Check.That(result[ScenarioContext.A].Number == 1 && result[ScenarioContext.B].Number == 20,
                "Expected mixed batch image was not reproduced.");
            Check.That(!Check.Same(context.Reference.Values, result), "Whole-batch skipping unexpectedly matched.");
        }
        results.Add(new ScenarioResult("Skip a transaction using the maximum base revision",
            "Expected counterexample: stale first member and new second member"));
        Check.That(NativeBlock.LiveBlocks == 0, "Native leak in negative controls.");
        return results;
    }
}
