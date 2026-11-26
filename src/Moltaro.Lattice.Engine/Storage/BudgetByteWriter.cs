using System.Buffers;

using Moltaro.Lattice.Budgeting;

namespace Moltaro.Lattice.Storage;

// A contiguous byte buffer whose replacement and current arrays overlap until copying finishes.
// The owner serializes writes and releases its backing only after the last borrower has left.
internal sealed class BudgetByteWriter(MemoryBudget? budget, int capacity, string owner) : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer = budget?.Allocate<byte>(capacity, owner) ?? new byte[capacity];
    private int _written;

    public int WrittenCount => _written;
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
            var length = BudgetCollections.CapacityFor(_buffer.Length, needed);
            var grown = budget?.Allocate<byte>(length, owner) ?? new byte[length];
            var previous = _buffer;
            WrittenSpan.CopyTo(grown);
            _buffer = grown;
            budget?.Release(previous);
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
        budget?.Release(previous);
    }
}
