using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// A wait of the test support of replication within the bound of the tests (TestWait.Bound) that says what
// it waited for: a wait that reaches the bound fails with a TimeoutException whose message names what did
// not end, the timeout of the bound as its inner exception, so that the output kept of a failed run says
// which wait it was. What the awaited task itself ends with passes unchanged.
internal static class BoundedWait
{
    public static async Task ForAsync(Task task, string awaited)
    {
        try
        {
            await task.WaitAsync(Bound);
        }
        catch (TimeoutException exception) when (!task.IsCompleted)
        {
            throw new TimeoutException($"{awaited} did not end within the bound of {Bound}.", exception);
        }
    }
}
