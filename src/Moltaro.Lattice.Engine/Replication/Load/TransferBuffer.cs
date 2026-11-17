using System.Buffers;

namespace Moltaro.Lattice.Replication.Load;

// The bytes of one root outside the arena, on either side of a load (the planner's block of 15 November
// 2026, "A root larger than a frame"; block of 17 November, point 9): on the master the copy of a root
// that the capture takes under the protection of reads and sends after it has left it; on a child the
// bytes of a root whose frames are still arriving. It is managed memory of the transfer and is not
// counted in the budget of the instance, as the keys an enumeration remembers are not.
//
// It is made of pieces of PieceSize bytes, taken as the bytes grow and kept when the buffer is cleared,
// so that one buffer serves every root of a connection: it grows to the largest root once, copies
// nothing when it grows, takes nothing from the heap for large objects, and holds a root of any size,
// beyond what one array can. Its bytes are read as a sequence of the pieces (Sequence).
//
// One thread at a time uses a buffer: the task of the connection that owns it.
internal sealed class TransferBuffer : IBufferWriter<byte>
{
    // The bytes of one piece: as much as GetSpan hands out at once, and more than the largest payload of
    // a block, so that a root payload is written in one piece.
    public const int PieceSize = 64 * 1024;

    private readonly List<TransferPiece> _pieces = [];

    // The piece being written; its index in _pieces. -1 while the buffer is empty.
    private int _current = -1;

    // The bytes written since the last Clear.
    public long Length { get; private set; }

    // The bytes written, as a sequence of the pieces that hold them, in their order. Valid until the
    // next write or Clear.
    public ReadOnlySequence<byte> Sequence
    {
        get
        {
            TransferPiece? first = null;
            TransferPiece? last = null;
            var runningIndex = 0L;

            for (var index = 0; index <= _current; index++)
            {
                var piece = _pieces[index];

                if (piece.Used == 0)
                {
                    continue;
                }

                piece.Link(runningIndex);
                runningIndex += piece.Used;

                last?.LinkNext(piece);
                first ??= piece;
                last = piece;
            }

            return first is null ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(first, 0, last!, last!.Used);
        }
    }

    // Empties the buffer and keeps its pieces for the next root.
    public void Clear()
    {
        for (var index = 0; index <= _current; index++)
        {
            _pieces[index].Used = 0;
        }

        _current = -1;
        Length = 0;
    }

    // Appends the bytes, across as many pieces as they need.
    public void Write(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var place = GetSpan();
            var count = Math.Min(place.Length, bytes.Length);

            bytes[..count].CopyTo(place);
            Advance(count);
            bytes = bytes[count..];
        }
    }

    // Room for at least sizeHint bytes, and at least one, in one piece: the rest of the piece being
    // written when it has that room, else the next piece. A size above PieceSize is a caller's mistake.
    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sizeHint, PieceSize);

        if (_current < 0 || PieceSize - _pieces[_current].Used < Math.Max(sizeHint, 1))
        {
            TakeNextPiece();
        }

        var piece = _pieces[_current];

        return piece.Bytes.AsMemory(piece.Used);
    }

    public void Advance(int count)
    {
        var piece = _pieces[_current];

        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, PieceSize - piece.Used);

        piece.Used += count;
        Length += count;
    }

    private void TakeNextPiece()
    {
        _current++;

        if (_current == _pieces.Count)
        {
            _pieces.Add(new TransferPiece());
        }
    }
}
