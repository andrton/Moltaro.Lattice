using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

internal sealed class ChurnLedger : NativeAllocator
{
    private readonly Lock _gate = new();
    private readonly Dictionary<nint, int> _segments = new();
    private long _held;
    private long _peak;
    private long _phasePeak;
    private long _obtained;
    private long _returned;
    private long _obtainedCount;
    private long _returnedCount;

    public override nint Allocate(int bytes)
    {
        lock (_gate)
        {
            var address = base.Allocate(bytes);
            if (address == 0)
            {
                throw new OutOfMemoryException();
            }

            _segments.Add(address, bytes);
            _held += bytes;
            _peak = Math.Max(_peak, _held);
            _phasePeak = Math.Max(_phasePeak, _held);
            _obtained += bytes;
            _obtainedCount++;
            return address;
        }
    }

    public override void Free(nint address)
    {
        lock (_gate)
        {
            int bytes = _segments[address];
            base.Free(address);
            _segments.Remove(address);
            _held -= bytes;
            _returned += bytes;
            _returnedCount++;
        }
    }

    public Totals Snapshot()
    {
        lock (_gate)
        {
            if (_held != _segments.Values.Sum(bytes => (long)bytes) || _held != _obtained - _returned)
            {
                throw new InvalidOperationException("Native allocation ledger does not reconcile.");
            }

            return new Totals(_held, _peak, _phasePeak, _obtainedCount, _obtained, _returnedCount, _returned);
        }
    }

    public void StartPhase()
    {
        lock (_gate)
        {
            _phasePeak = _held;
        }
    }

    internal sealed record Totals(long Held, long Peak, long PhasePeak,
        long Allocations, long ObtainedBytes, long Returns, long ReturnedBytes);
}
