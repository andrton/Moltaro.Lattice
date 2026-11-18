namespace Moltaro.Lattice.TestSupport.Concurrency;

// Runs a loop of a test on a thread of its own rather than on a thread of the pool. A loop whose every
// await completes at once — a read on a child that has reached its position, a write of a MemoryOnly
// master — never gives its thread back: started on the pool, such loops hold its threads for as long as
// they run, and the timers, sockets and continuations of the tests beside them wait for threads the pool
// adds only slowly. On a machine of 4 processors the pool starts with 4 threads; the Linux runner of the
// CI failed so in Epic 1, sprint S12. A loop given here is synchronous: it waits for an operation that
// does not complete at once by blocking its own thread (Wait), which takes nothing from the pool, and
// the loops stay truly concurrent.
internal static class OwnThread
{
    public static Task Run(Action loop) =>
        Task.Factory.StartNew(loop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static Task<T> Run<T>(Func<T> loop) =>
        Task.Factory.StartNew(loop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // The result of an operation inside such a loop: at once when it has completed, otherwise once it
    // completes, with this thread blocked meanwhile. A failure of the operation is thrown as it is.
    public static T Wait<T>(ValueTask<T> operation) =>
        operation.IsCompletedSuccessfully ? operation.Result : operation.AsTask().GetAwaiter().GetResult();
}
