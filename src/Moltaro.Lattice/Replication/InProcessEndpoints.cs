namespace Moltaro.Lattice.Replication;

// Connects children to masters opened in the same process. It stands in for the network
// transport, which does not exist yet.
internal static class InProcessEndpoints
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<Uri, IReplicationMaster> Masters = [];
    private static TaskCompletionSource _changed = NewSignal();

    public static void Register(Uri address, IReplicationMaster master)
    {
        TaskCompletionSource signal;
        lock (Gate)
        {
            if (!Masters.TryAdd(address, master))
            {
                throw new LatticeException(LatticeErrorEnum.InvalidConfiguration, $"Another master already listens at '{address}'.");
            }

            signal = _changed;
            _changed = NewSignal();
        }

        signal.SetResult();
    }

    public static void Unregister(Uri address, IReplicationMaster master)
    {
        lock (Gate)
        {
            if (Masters.TryGetValue(address, out var registered) && ReferenceEquals(registered, master))
            {
                Masters.Remove(address);
            }
        }
    }

    // Completes when a master listens at the address; waits for one to appear otherwise.
    public static async ValueTask<IReplicationMaster> ConnectAsync(Uri address, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (Gate)
            {
                if (Masters.TryGetValue(address, out var master))
                {
                    return master;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
