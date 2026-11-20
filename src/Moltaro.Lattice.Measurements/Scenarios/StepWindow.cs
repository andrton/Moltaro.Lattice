using System.Diagnostics;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The window of the scenarios that count what threads do together. Each thread takes one step
// (IWindowStep), a read, a search or a write, over and over through a warm-up, and what the threads do
// is counted in a window of a fixed length. The threads and the main thread start together at a
// barrier; the main thread sleeps through the warm-up (Start), opens the window, sleeps through it and
// closes it (Count), and waits for the threads to stop (Join). What a scenario reports beside the
// counts, such as the collections or the processors busy, it reads between those calls.
//
// Every thread reads the phase before each step, counts in a local from the moment it sees the window
// open, and writes its count once, when it stops, with the managed bytes it allocated in the window.
// The window times no step: a step that times some of its steps is told whether the window is open and
// what its thread has counted. The threads are not pinned to processors, so a figure is the result of
// one window and runs are repeated. A window runs once.
internal sealed class StepWindow
{
    public static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(0.5);

    public static readonly TimeSpan Length = TimeSpan.FromSeconds(2);

    // The phases of a window, which its threads read: they take their steps through the warm-up, count
    // them through the window, then stop.
    private const int WarmingUp = 0;

    private const int Counting = 1;

    private const int Stopped = 2;

    private readonly List<Thread> _threads = [];

    // Written by the main thread when the window opens and when it closes; read by every thread before
    // each of its steps.
    private int _phase = WarmingUp;

    private Barrier? _start;

    // What each thread counted in the window and the managed bytes it allocated in it, in the order the
    // threads were added: written once by each thread when it stops, read after Join.
    public long[] Counted { get; private set; } = [];

    public long[] Allocated { get; private set; } = [];

    // A count a second as a report gives it.
    public static string PerSecond(double value) => ReadSeries.Number((long)Math.Round(value));

    // Adds a thread, named as given, that takes the step over and over.
    public void Add<TStep>(string name, TStep step)
        where TStep : struct, IWindowStep
    {
        var index = _threads.Count;

        _threads.Add(new Thread(() =>
        {
            _start!.SignalAndWait();
            (Counted[index], Allocated[index]) = Repeat(step, ref _phase);
        })
        {
            IsBackground = true,
            Name = name,
        });
    }

    // Starts the threads together and lets them take their steps through the warm-up.
    public void Start()
    {
        Counted = new long[_threads.Count];
        Allocated = new long[_threads.Count];
        _start = new Barrier(_threads.Count + 1);

        foreach (var thread in _threads)
        {
            thread.Start();
        }

        _start.SignalAndWait();
        Thread.Sleep(WarmUp);
    }

    // Opens the window, sleeps through it and closes it: how long it was open.
    public TimeSpan Count()
    {
        var started = Stopwatch.GetTimestamp();

        Volatile.Write(ref _phase, Counting);
        Thread.Sleep(Length);
        Volatile.Write(ref _phase, Stopped);

        return Stopwatch.GetElapsedTime(started);
    }

    // Waits until every thread has stopped and written what it counted.
    public void Join()
    {
        foreach (var thread in _threads)
        {
            thread.Join();
        }

        _start!.Dispose();
    }

    // Takes the step until the phase is Stopped, and returns how many it counted while the phase was
    // Counting, and the managed bytes its thread allocated meanwhile.
    private static (long Counted, long Allocated) Repeat<TStep>(TStep step, ref int phase)
        where TStep : struct, IWindowStep
    {
        var counted = 0L;
        var allocated = 0L;
        var counting = false;

        while (true)
        {
            var now = Volatile.Read(ref phase);

            if (now == Stopped)
            {
                return (counted, counting ? GC.GetAllocatedBytesForCurrentThread() - allocated : 0);
            }

            if (now == Counting && !counting)
            {
                counting = true;
                counted = 0;
                allocated = GC.GetAllocatedBytesForCurrentThread();
            }

            counted += step.Run(counting, counted);
        }
    }
}
