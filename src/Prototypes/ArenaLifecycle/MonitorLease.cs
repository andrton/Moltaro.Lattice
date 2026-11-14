namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal readonly struct MonitorLease(object gate) : IDisposable
{
    public void Dispose()
    {
        TailProbe.Active?.Released();
        Monitor.Exit(gate);
    }
}
