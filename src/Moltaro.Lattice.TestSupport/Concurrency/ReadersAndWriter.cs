namespace Moltaro.Lattice.TestSupport.Concurrency;

// For tests in which readers read while one writer writes, to state what a read returns while the
// entities it reads change. Each reader runs on a thread of its own, so that a reader that never
// waits does not hold back the writer by taking a thread of the pool from it. Nothing sleeps: the
// writes start once every reader has read once, and the readers read until the writes are done.
internal static class ReadersAndWriter
{
    // Starts the readers, each with the read that readFor makes for its number, runs write once every
    // reader has read once, and then lets every reader read until write has completed. A read or a
    // write that fails fails the run, after every reader has ended. A reader that cannot start within
    // a minute, which only a machine without threads to spare would cause, is a TimeoutException.
    public static async Task RunAsync(int readers, Func<int, Action> readFor, Func<Task> write)
    {
        using var started = new CountdownEvent(readers);
        using var written = new ManualResetEventSlim();

        var running = Enumerable.Range(0, readers)
            .Select(reader => OwnThread.Run(() => ReadUntilWritten(readFor(reader), started, written)))
            .ToArray();

        try
        {
            if (!started.Wait(TimeSpan.FromMinutes(1)))
            {
                throw new TimeoutException("The readers did not start within a minute.");
            }

            await write();
        }
        finally
        {
            written.Set();
            await Task.WhenAll(running);
        }
    }

    private static void ReadUntilWritten(Action read, CountdownEvent started, ManualResetEventSlim written)
    {
        try
        {
            read();
        }
        finally
        {
            started.Signal();
        }

        while (!written.IsSet)
        {
            read();
        }
    }
}
