using System.Buffers;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Storage;

// A contiguous byte buffer whose array is charged to the budget given: reserved before the array is
// allocated, the replacement charged beside the old array while the written bytes are copied, and the
// old one released then; the last array is released by Dispose (docs/design/storage-and-maintenance.md,
// the memory budget). The owner serializes the writes and disposes it only after the last borrower of
// its memory has left.
internal sealed class BudgetByteWriter(MemoryBudget budget, int capacity) : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer = budget.Allocate<byte>(capacity);

    private int _written;

    public int WrittenCount => _written;

    // The length of the array, which the budget charges.
    public int Capacity => _buffer.Length;

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _written);
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        var needed = checked(_written + Math.Max(sizeHint, 1));
        if (needed > _buffer.Length)
        {
            budget.Grow(ref _buffer, Math.Max(needed, (int)Math.Min(2L * _buffer.Length, Array.MaxLength)));
        }

        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public void ResetWrittenCount() => _written = 0;

    public void Dispose()
    {
        var previous = _buffer;
        _buffer = [];
        _written = 0;
        budget.Release(previous);
    }
}
