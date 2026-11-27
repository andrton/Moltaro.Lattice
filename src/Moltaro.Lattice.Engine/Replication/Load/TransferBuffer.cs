using System.Buffers;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Replication.Load;

// The bytes of one root outside the arena, on either side of a load (the planner's block of 15 November
// 2026, "A root larger than a frame"; block of 17 November, point 9): on the master the copy of a root
// that the capture takes under the protection of reads and sends after it has left it; on a child the
// bytes of a root or of a transaction whose frames are still arriving.
//
// It is made of pieces of PieceSize bytes, taken as the bytes grow and kept when the buffer is cleared,
// so that one buffer serves every root of a connection: it grows to the largest root once, copies
// nothing when it grows, takes nothing from the heap for large objects, and holds a root of any size,
// beyond what one array can. Its bytes are read as a sequence of the pieces (Sequence).
//
// The bytes of each piece and the list of the pieces are charged to the budget given before they are
// allocated, and released when the buffer is disposed (docs/design/storage-and-maintenance.md, the memory
// budget); the piece objects are bookkeeping of the headroom. A buffer made without a budget, for the
// export of one root of a batch, is the caller's input and charges an unlimited budget of its own.
//
// One thread at a time uses a buffer: the task of the connection that owns it.
internal sealed class TransferBuffer(MemoryBudget budget) : IBufferWriter<byte>, IDisposable
{
    // The bytes of one piece: as much as GetSpan hands out at once, and more than the largest payload of
    // a block, so that a root payload is written in one piece.
    public const int PieceSize = 64 * 1024;

    private static readonly long PieceBytes = MemoryBudget.ArrayBytes<byte>(PieceSize);

    private readonly List<TransferPiece> _pieces = [];

    // The piece being written; its index in _pieces. -1 while the buffer is empty.
    private int _current = -1;

    public TransferBuffer()
        : this(new MemoryBudget())
    {
    }

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

    // Takes the pieces that the bytes given need beyond those written, before anything of them is
    // written, so that a refusal by the budget changes nothing that was accepted. From the start, the
    // bytes are counted from an empty buffer. The pieces taken stay for the next body.
    public void PrepareAppend(int bytes, bool fromStart = false)
    {
        var current = fromStart ? -1 : _current;
        var remaining = current < 0 ? 0 : PieceSize - _pieces[current].Used;
        var more = (int)((Math.Max(0L, (long)bytes - remaining) + PieceSize - 1) / PieceSize);
        Grow(current + 1 + more);
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

    // Adds the list of the pieces and the bytes of each piece.
    public void ReportHeld(List<HeldArray> held)
    {
        if (_pieces.Capacity != 0)
        {
            held.Add(new HeldArray(nameof(TransferBuffer), typeof(TransferPiece), _pieces.Capacity));
        }

        for (var index = 0; index < _pieces.Count; index++)
        {
            held.Add(new HeldArray(nameof(TransferBuffer), typeof(byte), PieceSize));
        }
    }

    // Releases every piece and the list, once the owner is done with the bytes.
    public void Dispose()
    {
        budget.Release(_pieces.Count * PieceBytes);
        if (_pieces.Capacity != 0)
        {
            budget.Release(MemoryBudget.ArrayBytes<TransferPiece>(_pieces.Capacity));
        }

        _pieces.Clear();
        _pieces.Capacity = 0;
        _current = -1;
        Length = 0;
    }

    private void TakeNextPiece()
    {
        Grow(_current + 2);
        _current++;
    }

    // Charges and takes pieces until the buffer holds that many.
    private void Grow(int needed)
    {
        if (needed <= _pieces.Count)
        {
            return;
        }

        budget.EnsureCapacity(_pieces, needed);
        while (_pieces.Count < needed)
        {
            budget.Reserve(PieceBytes);
            TransferPiece piece;
            try
            {
                piece = new TransferPiece();
            }
            catch
            {
                budget.Release(PieceBytes);
                throw;
            }

            _pieces.Add(piece);
        }
    }
}
