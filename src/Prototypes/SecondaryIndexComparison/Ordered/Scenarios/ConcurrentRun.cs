using System.Diagnostics;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// Runs readers, each on a thread of its own (not the thread pool), and a writer on one more when there
// is one, all started together, for a duration; then stops them and waits for them.
internal static class ConcurrentRun
{
    public static ConcurrentResult Run(int readers, Func<int, RunControl, (long Operations, long Extra)> reader, Func<RunControl, long>? writer, double seconds)
    {
        var control = new RunControl();
        var results = new (long Operations, long Extra)[readers];
        var writes = 0L;
        var threads = new List<Thread>();
        using var ready = new CountdownEvent(readers + (writer is null ? 0 : 1));
        using var start = new ManualResetEventSlim();

        for (var number = 0; number < readers; number++)
        {
            var own = number;
            var thread = new Thread(() =>
            {
                ready.Signal();
                start.Wait();
                results[own] = reader(own, control);
            })
            { IsBackground = true, Name = $"reader-{own}" };

            threads.Add(thread);
            thread.Start();
        }

        if (writer is not null)
        {
            var thread = new Thread(() =>
            {
                ready.Signal();
                start.Wait();
                writes = writer(control);
            })
            { IsBackground = true, Name = "writer" };

            threads.Add(thread);
            thread.Start();
        }

        ready.Wait();

        var stopwatch = Stopwatch.StartNew();

        start.Set();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        control.Stop();

        foreach (var thread in threads)
        {
            thread.Join();
        }

        stopwatch.Stop();

        return new ConcurrentResult(results.Sum(result => result.Operations), results.Sum(result => result.Extra), writes, stopwatch.Elapsed.TotalSeconds);
    }

    // Runs readers, each on a thread of its own, beside a writer on one more that does a given amount
    // of work; the run ends when the writer is done, and its seconds are the writer's.
    public static ConcurrentResult UntilWriterEnds(int readers, Func<int, RunControl, (long Operations, long Extra)> reader, Func<long> writer)
    {
        var control = new RunControl();
        var results = new (long Operations, long Extra)[readers];
        var threads = new List<Thread>();
        using var ready = new CountdownEvent(readers + 1);
        using var start = new ManualResetEventSlim();

        for (var number = 0; number < readers; number++)
        {
            var own = number;
            var thread = new Thread(() =>
            {
                ready.Signal();
                start.Wait();
                results[own] = reader(own, control);
            })
            { IsBackground = true, Name = $"reader-{own}" };

            threads.Add(thread);
            thread.Start();
        }

        var writes = 0L;
        var seconds = 0.0;
        var writerThread = new Thread(() =>
        {
            ready.Signal();
            start.Wait();

            var stopwatch = Stopwatch.StartNew();

            writes = writer();
            seconds = stopwatch.Elapsed.TotalSeconds;
            control.Stop();
        })
        { IsBackground = true, Name = "writer" };

        threads.Add(writerThread);
        writerThread.Start();
        ready.Wait();
        start.Set();

        foreach (var thread in threads)
        {
            thread.Join();
        }

        return new ConcurrentResult(results.Sum(result => result.Operations), results.Sum(result => result.Extra), writes, seconds);
    }
}
