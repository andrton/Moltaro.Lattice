using System.Diagnostics;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed class TestThread : IDisposable
{
    private readonly Thread _thread;
    private Exception? _failure;
    private bool _joined;

    public TestThread(Action action)
    {
        _thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                _failure = exception;
            }
        });
        _thread.Start();
    }

    public void Dispose()
    {
        if (_joined)
        {
            return;
        }

        if (!_thread.Join(TimeSpan.FromSeconds(4)))
        {
            Console.Error.WriteLine("Owned test thread exceeded its join budget.");
            // This executable is always an isolated bounded worker. Do not free
            // native memory under an unresolved worker or leave a load running.
            Environment.Exit(2);
        }

        _joined = true;
        if (_failure is not null)
        {
            throw new InvalidOperationException("Test thread failed.", _failure);
        }
    }

    public static void Await(Func<bool> condition, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Check.That(clock.Elapsed < TimeSpan.FromSeconds(2), message);
            Thread.Sleep(1);
        }
    }
}
