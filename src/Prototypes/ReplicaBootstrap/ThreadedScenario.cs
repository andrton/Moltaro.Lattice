namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal static class ThreadedScenario
{
    public static List<ScenarioResult> Run()
    {
        using (var context = new ScenarioContext())
        using (var copied = new ManualResetEventSlim())
        {
            context.Write(Mutation.Put(ScenarioContext.A, 1, 1), Mutation.Put(ScenarioContext.B, 2, 2));
            context.Open();
            Exception? writerFailure = null;
            var writer = new Thread(() =>
            {
                try
                {
                    Check.That(copied.Wait(TimeSpan.FromSeconds(3)), "Writer did not receive the capture signal.");
                    context.Write(Mutation.Add(ScenarioContext.A, 3));
                    context.Write(Mutation.Remove(ScenarioContext.B));
                    context.Write(Mutation.Put(ScenarioContext.B, 4, 4));
                    context.Write(Mutation.Put(ScenarioContext.A, 5, 5), Mutation.Put(ScenarioContext.B, 6, 6));
                }
                catch (Exception exception) { writerFailure = exception; }
            }) { IsBackground = true, Name = "Bootstrap fixture writer" };

            writer.Start();
            try
            {
                // Hold only detached bytes while the writer changes and frees native blocks.
                var detached = context.CaptureDetached();
                copied.Set();
                Check.That(writer.Join(TimeSpan.FromSeconds(3)), "Writer blocked while transfer held a detached frame.");
                if (writerFailure is not null) { throw new InvalidOperationException("Fixture writer failed.", writerFailure); }
                Check.That(BlockCodec.Decode(detached.Block!).Number == 1,
                    "Detached capture changed with the source arena.");
                context.Replica.Install(detached);
                context.Finish();
            }
            finally
            {
                copied.Set();
                Check.That(writer.Join(TimeSpan.FromSeconds(3)), "Fixture writer did not finish.");
            }
        }
        Check.That(NativeBlock.LiveBlocks == 0, "Native leak in threaded handoff.");
        return [new ScenarioResult("Real writer completes while a copied frame awaits delivery", "Passed")];
    }
}
