namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// Tells a test that a write the arena refused has begun to wait, through the switch of the engine
// that runs once when it does (EngineDatabase.HoldNextWaitForReads): for the reads that hold retired
// memory or for the writes admitted before it (brief of Epic 1, sprint S09, "The refusal"), so that a
// test knows the write waits instead of giving it a window of time.
internal static class RefusedWriteWait
{
    // Completes when the next write the arena refused begins to wait. The switch runs on the thread of
    // the write and only completes the task, whose continuations run elsewhere.
    public static Task Begins(EngineDatabase database)
    {
        var begins = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        database.HoldNextWaitForReads(() => begins.TrySetResult());

        return begins.Task;
    }
}
