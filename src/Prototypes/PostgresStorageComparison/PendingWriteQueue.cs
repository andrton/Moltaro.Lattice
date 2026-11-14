namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed class PendingWriteQueue
{
    public const int MaximumCount = 32768;
    public const long MaximumPayloadBytes = 16 * 1024 * 1024;

    // This gate protects queue membership, byte accounting and completion only. No I/O spans it.
    private readonly object _gate = new();
    private readonly Queue<PendingWrite> _items = new(MaximumCount);
    private long _payloadBytes;
    private long _peakPayloadBytes;
    private int _peakCount;
    private bool _completed;

    public bool TryAdd(Mutation mutation, int commandBytes)
    {
        lock (_gate)
        {
            if (_completed || _items.Count == MaximumCount
                || _payloadBytes + commandBytes > MaximumPayloadBytes)
            {
                return false;
            }

            // Reject before allocating the owned command or completion state.
            _items.Enqueue(new PendingWrite(mutation, commandBytes));
            _payloadBytes += commandBytes;
            _peakCount = Math.Max(_peakCount, _items.Count);
            _peakPayloadBytes = Math.Max(_peakPayloadBytes, _payloadBytes);
            return true;
        }
    }

    public PendingWrite? TryTake()
    {
        lock (_gate)
        {
            if (!_items.TryDequeue(out var item))
            {
                return null;
            }

            _payloadBytes -= item.Command.Length;
            return item;
        }
    }

    public bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                return _completed && _items.Count == 0;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public PendingQueueSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new PendingQueueSnapshot(_items.Count, _payloadBytes, _peakCount, _peakPayloadBytes);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
        }
    }
}
