namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

// Tells a test that a write the arena refused has begun to wait, through the switch of the engine
// that runs once when it does (EngineDatabase.HoldNextRefusedWriteWait), so that a test knows the write
// waits instead of giving it a window of time. A refused write waits in its resolution (brief of Epic
// 1, sprint S09, "When a write is refused for memory: the rule after review round 1"): for the writes
// admitted before it, for the writes in flight while it holds back later ones, or for the reads that
// hold retired memory; the switch runs at the first of these waits that the write has to make.
internal static class RefusedWriteWait
{
    // Completes when the next write the arena refused begins to wait. The switch runs on the thread of
    // the write and only completes the task, whose continuations run elsewhere.
    public static Task Begins(EngineDatabase database)
    {
        var begins = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        database.HoldNextRefusedWriteWait(() => begins.TrySetResult());

        return begins.Task;
    }
}
