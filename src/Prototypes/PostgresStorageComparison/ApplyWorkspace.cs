namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed class ApplyWorkspace
{
    #region Bounded workspace ownership

    public const int PayloadBudgetBytes = 32 * 1024 * 1024;
    private readonly ReplayLayout _layout;
    private readonly int[] _unitSlots;
    private readonly bool[] _keys;
    private readonly byte[][] _buffers;

    public ApplyWorkspace(ReplayLayout layout, int unitBytes, int journalBatches)
    {
        _layout = layout;
        UnitBytes = unitBytes;
        MaximumUnits = Math.Min(journalBatches * ReplayLayout.WriterBatch, PayloadBudgetBytes / unitBytes);
        Changes = new ReplayChange[journalBatches * ReplayLayout.WriterBatch];
        UnitIds = new int[MaximumUnits];
        _unitSlots = new int[layout.ImageBytes / unitBytes];
        _keys = new bool[layout.RecordCount];
        _buffers = new byte[MaximumUnits][];
        Reset();
    }

    public int UnitBytes { get; }
    public int MaximumUnits { get; }
    public ReplayChange[] Changes { get; }
    public int[] UnitIds { get; }
    public int ChangeCount { get; private set; }
    public int UnitCount { get; private set; }
    public int DistinctKeys { get; private set; }
    public int JournalBatches { get; private set; }
    public bool HitUnitBudget { get; private set; }
    public long RetainedBufferAllocatedBytes { get; private set; }
    public long RetainedBufferPayloadBytes { get; private set; }

    #endregion

    #region Ordered batch selection and unit coalescing

    public void Reset()
    {
        Array.Fill(_unitSlots, -1);
        Array.Clear(_keys);
        ChangeCount = 0;
        UnitCount = 0;
        DistinctKeys = 0;
        JournalBatches = 0;
        HitUnitBudget = false;
    }

    public bool AddBatch(EncodedBatch batch, ReplayPatternEnum pattern)
    {
        if (batch.Count is < 1 or > ReplayLayout.WriterBatch
            || batch.Payload.Length != batch.Count * ReplayLayout.CommandBytes)
        {
            throw new InvalidDataException("Invalid journal batch size.");
        }

        Span<ReplayChange> decoded = stackalloc ReplayChange[ReplayLayout.WriterBatch];
        Span<int> addedUnits = stackalloc int[ReplayLayout.WriterBatch];
        var newUnits = 0;
        var previous = ChangeCount == 0 ? 0 : Changes[ChangeCount - 1].Sequence;
        for (var index = 0; index < batch.Count; index++)
        {
            var change = _layout.Decode(batch.Payload.AsSpan(index * ReplayLayout.CommandBytes), pattern);
            if (change.Sequence <= previous || (index == 0 && change.Sequence != batch.FirstSequence))
            {
                throw new InvalidDataException("Journal sequence is not increasing.");
            }

            previous = change.Sequence;
            decoded[index] = change;
            var unit = change.Key * _layout.RecordBytes / UnitBytes;
            if (_unitSlots[unit] < 0 && !addedUnits[..newUnits].Contains(unit))
            {
                addedUnits[newUnits++] = unit;
            }
        }

        if (UnitCount + newUnits > MaximumUnits)
        {
            HitUnitBudget = true;
            return false;
        }

        for (var index = 0; index < newUnits; index++)
        {
            var slot = UnitCount++;
            var unit = addedUnits[index];
            UnitIds[slot] = unit;
            _unitSlots[unit] = slot;
            if (_buffers[slot] is null)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                _buffers[slot] = new byte[UnitBytes];
                RetainedBufferAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
                RetainedBufferPayloadBytes += UnitBytes;
            }
        }

        for (var index = 0; index < batch.Count; index++)
        {
            var change = decoded[index];
            Changes[ChangeCount++] = change;
            if (!_keys[change.Key])
            {
                _keys[change.Key] = true;
                DistinctKeys++;
            }
        }

        JournalBatches++;
        return true;
    }

    public byte[] BufferForUnit(int unit) => _buffers[_unitSlots[unit]];

    public byte[][] ApplyAndGetPayloads()
    {
        for (var index = 0; index < ChangeCount; index++)
        {
            var change = Changes[index];
            var offset = change.Key * _layout.RecordBytes;
            ReplayLayout.SetValue(BufferForUnit(offset / UnitBytes).AsSpan(offset % UnitBytes, 16), change.Sequence);
        }

        return _buffers.AsSpan(0, UnitCount).ToArray();
    }

    #endregion
}
