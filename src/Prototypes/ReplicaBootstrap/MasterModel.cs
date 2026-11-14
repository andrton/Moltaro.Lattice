namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal sealed class MasterModel(int maximumPending = 128) : IDisposable
{
    #region State

    // Serializes model publication/capture only. It is not a production writer design.
    // No socket, callback or event wait runs under this gate.
    private readonly object _gate = new();
    private readonly List<RootEntry?> _slots = [];
    private readonly List<PublishedTransaction> _pending = [];
    private CaptureStart? _start;
    private long _published;
    private long? _boundary;
    private int _nextSlot;
    private bool _subscribed;
    private bool _retainCatalog;
    private int _pendingBytes;

    public int PeakPendingBytes { get; private set; }
    public int PeakPendingTransactions { get; private set; }
    public long PublishedPosition { get { lock (_gate) { return _published; } } }
    public int PendingCount { get { lock (_gate) { return _pending.Count; } } }
    public int RetainedDeletionCount
    {
        get { lock (_gate) { return _slots.Count(entry => entry is { Block: null }); } }
    }

    #endregion

    #region Capture and delivery

    public CaptureStart StartCapture()
    {
        lock (_gate)
        {
            Check.That(_start is null, "One initialization session per model instance.");
            _start = new CaptureStart(_published, _slots.Count);
            // Register retention at the same publication boundary as the catalog fence.
            _subscribed = true;
            _retainCatalog = true;
            return _start;
        }
    }

    public CaptureFrame CaptureNext()
    {
        lock (_gate)
        {
            RequireSubscription();
            if (_start is null || _nextSlot >= _start.SlotCount)
            {
                throw new ProtocolException("Capture fence exhausted.");
            }
            var slot = _nextSlot++;
            var entry = _slots[slot];
            return new CaptureFrame(slot, entry?.Key, entry?.Revision ?? 0, entry?.Block?.Copy());
        }
    }

    public long CompleteCapture()
    {
        lock (_gate)
        {
            RequireSubscription();
            if (_start is null || _nextSlot != _start.SlotCount || _boundary is not null)
            {
                throw new ProtocolException("Incomplete or already completed capture.");
            }
            _boundary = _published;
            return _boundary.Value;
        }
    }

    public PublishedTransaction[] Pending()
    {
        lock (_gate)
        {
            RequireSubscription();
            return _pending.ToArray();
        }
    }

    public void Acknowledge(long position)
    {
        lock (_gate)
        {
            RequireSubscription();
            if (_boundary is null || position < _boundary || position > _published)
            {
                throw new ProtocolException("Cannot acknowledge an incomplete initial state.");
            }
            _pending.RemoveAll(transaction => transaction.Position <= position);
            _pendingBytes = _pending.Sum(transaction => transaction.EncodedBytes);
            _retainCatalog = false;
            ReclaimDeletions();
        }
    }

    private void RequireSubscription()
    {
        if (!_subscribed) { throw new ProtocolException("Delivery retention lost; full reload required."); }
    }

    #endregion

    #region Atomic model writes

    public PublishedTransaction Publish(params Mutation[] mutations)
    {
        lock (_gate)
        {
            Check.That(mutations.Length is >= 1 and <= 4, "Prototype batch limit is four.");
            // Fixtures follow the accepted one-operation-per-root batch contract.
            Check.That(mutations.Select(mutation => mutation.Key).Distinct().Count() == mutations.Length,
                "Prototype batches use distinct root identities.");
            var prepared = new List<PreparedMutation>(mutations.Length);
            try
            {
                foreach (var mutation in mutations)
                {
                    prepared.Add(Prepare(mutation));
                }
                Check.That(_slots.Count + prepared.Count(item => item.IsNew) <= 64,
                    "Prototype catalog budget exhausted.");
                _slots.EnsureCapacity(_slots.Count + prepared.Count(item => item.IsNew));
                _pending.EnsureCapacity(_pending.Count + 1);
                var position = _published + 1;
                var transaction = new PublishedTransaction(position, prepared.Select(item => item.Effect).ToArray());
                foreach (var item in prepared)
                {
                    var previous = item.Target.Block;
                    item.Target.Block = item.TakeBlock();
                    item.Target.Revision = position;
                    if (item.IsNew)
                    {
                        // Do not reuse any fenced slot while capture still needs its identity/deletion mark.
                        var reusable = _retainCatalog ? -1 : _slots.FindIndex(entry => entry is null);
                        if (reusable < 0) { _slots.Add(item.Target); }
                        else { _slots[reusable] = item.Target; }
                    }
                    previous?.Dispose();
                }
                _published = position;
                if (_subscribed)
                {
                    if (_pending.Count >= maximumPending || _pendingBytes + transaction.EncodedBytes > 65536)
                    {
                        _subscribed = false;
                        _retainCatalog = false;
                        _pending.Clear();
                        _pendingBytes = 0;
                    }
                    else
                    {
                        _pending.Add(transaction);
                        _pendingBytes += transaction.EncodedBytes;
                        PeakPendingBytes = Math.Max(PeakPendingBytes, _pendingBytes);
                        PeakPendingTransactions = Math.Max(PeakPendingTransactions, _pending.Count);
                    }
                }
                if (!_retainCatalog) { ReclaimDeletions(); }
                return transaction;
            }
            finally
            {
                foreach (var item in prepared) { item.Dispose(); }
            }
        }
    }

    private PreparedMutation Prepare(Mutation mutation)
    {
        var current = _slots.FirstOrDefault(entry => entry is not null
            && entry.Key == mutation.Key && entry.Block is not null);
        if (current is null && mutation.Kind != MutationKindEnum.Upsert)
        {
            throw new ProtocolException("Partial mutation needs a live root.");
        }
        byte[]? bytes;
        switch (mutation.Kind)
        {
            case MutationKindEnum.Upsert:
                bytes = BlockCodec.Create(mutation.Number, mutation.Items);
                break;
            case MutationKindEnum.SetNumber:
                bytes = current!.Block!.Copy();
                BlockCodec.SetNumber(bytes, mutation.Number);
                break;
            case MutationKindEnum.Append:
                bytes = current!.Block!.Copy();
                BlockCodec.Append(bytes, mutation.Number);
                break;
            case MutationKindEnum.Delete:
                bytes = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        var effect = new ResolvedEffect(mutation.Kind, mutation.Key, current?.Revision ?? 0,
            mutation.Number, mutation.Kind == MutationKindEnum.Upsert ? bytes : null);
        var target = current ?? new RootEntry(mutation.Key, 0, null);
        return new PreparedMutation(target, current is null, bytes is null ? null : new NativeBlock(bytes), effect);
    }

    private void ReclaimDeletions()
    {
        for (var index = 0; index < _slots.Count; index++)
        {
            if (_slots[index] is { Block: null }) { _slots[index] = null; }
        }
    }

    #endregion

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _slots) { entry?.Block?.Dispose(); }
            _slots.Clear();
            _pending.Clear();
            _subscribed = false;
        }
    }
}
