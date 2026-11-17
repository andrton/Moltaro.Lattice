using System.Buffers;

namespace Moltaro.Lattice.Replication.Transactions;

// Writes into an array from a position, for bytes whose length the caller knows before it writes them,
// such as a root that Load.BlockExport writes into a transaction made at its exact length. It allocates
// nothing. Asking for more room than the array has left is a caller's mistake that would write past what
// was counted: InvalidOperationException.
internal sealed class ExactBufferWriter : IBufferWriter<byte>
{
    private readonly byte[] _bytes;

    public ExactBufferWriter(byte[] bytes, int position)
    {
        _bytes = bytes;
        Position = position;
    }

    // Where the next byte is written.
    public int Position { get; private set; }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

        var left = _bytes.Length - Position;

        if (left < Math.Max(sizeHint, 1))
        {
            throw new InvalidOperationException($"A writer of an array of {_bytes.Length} bytes is asked for {Math.Max(sizeHint, 1)} at {Position}, past what was counted.");
        }

        return _bytes.AsMemory(Position);
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _bytes.Length - Position);

        Position += count;
    }
}
