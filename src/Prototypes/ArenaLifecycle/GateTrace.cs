namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

// Diagnostic acquisition probes are enabled only inside a measured operation.
// The zero-time attempt distinguishes observed contention from elapsed wall time.
internal static class GateTrace
{
    public static MonitorLease EnterMonitor(object gate, string kind)
    {
        var probe = TailProbe.Active;
        if (probe is null)
        {
            Monitor.Enter(gate);
        }
        else
        {
            var index = probe.StartGate(gate, kind);
            var contended = !Monitor.TryEnter(gate);
            if (contended)
            {
                Monitor.Enter(gate);
            }

            probe.Acquired(index, contended);
        }

        return new MonitorLease(gate);
    }

    public static void EnterRead(ReaderWriterLockSlim gate, string kind) => Enter(gate, kind, false, true);
    public static void EnterWrite(ReaderWriterLockSlim gate, string kind) => Enter(gate, kind, true, true);
    public static bool TryWrite(ReaderWriterLockSlim gate, string kind) => Enter(gate, kind, true, false);

    private static bool Enter(ReaderWriterLockSlim gate, string kind, bool write, bool wait)
    {
        var probe = TailProbe.Active;
        if (probe is null)
        {
            if (!wait)
            {
                return gate.TryEnterWriteLock(0);
            }

            if (write)
            {
                gate.EnterWriteLock();
            }
            else
            {
                gate.EnterReadLock();
            }

            return true;
        }

        var index = probe.StartGate(gate, kind);
        var entered = write ? gate.TryEnterWriteLock(0) : gate.TryEnterReadLock(0);
        if (!entered && wait)
        {
            if (write)
            {
                gate.EnterWriteLock();
            }
            else
            {
                gate.EnterReadLock();
            }
        }

        probe.Acquired(index, !entered, !entered && !wait);
        return entered || wait;
    }

    public static void ExitRead(ReaderWriterLockSlim gate)
    {
        TailProbe.Active?.Released();
        gate.ExitReadLock();
    }

    public static void ExitWrite(ReaderWriterLockSlim gate)
    {
        TailProbe.Active?.Released();
        gate.ExitWriteLock();
    }
}
