namespace Moltaro.Lattice.TestSupport.Replication;

// For tests in which a master and its children run in one process.
internal static class MasterAndChild
{
    // The address at which a master of the engine listens on the loopback interface, at a port the
    // system chooses when it opens: every master that is given it has a port of its own, so tests
    // running at the same time do not reach each other's master. The open master reports the port in
    // its ListenUri, and that is the address its children are given.
    public static Uri Loopback { get; } = new("lattice://127.0.0.1:0");

    // A child may lag behind its master, so tests wait for a change to arrive.
    public static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}
