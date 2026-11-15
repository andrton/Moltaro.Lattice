namespace Moltaro.Lattice.TestSupport.Replication;

// For tests in which a master and its children run in one process.
internal static class MasterAndChild
{
    // An address of its own for every master, so that tests running at the same time do not
    // reach each other's master.
    public static Uri NewAddress() => new($"lattice://test/{Guid.NewGuid():N}");

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
