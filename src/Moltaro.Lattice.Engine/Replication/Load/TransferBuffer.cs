using System.Buffers;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Replication.Load;

// The bytes of one root outside the arena, on either side of a load (the planner's block of 15 November
// 2026, "A root larger than a frame"; block of 17 November, point 9): on the master the copy of a root
// that the capture takes under the protection of reads and sends after it has left it; on a child the
// bytes of a root whose frames are still arriving. The connection or batch owns the backing and
// its charge until the buffer is disposed or ownership is transferred.
//
// It is made of pieces of PieceSize bytes, taken as the bytes grow and kept when the buffer is cleared,
// so that one buffer serves every root of a connection: it grows to the largest root once, copies
// nothing when it grows, takes nothing from the heap for large objects, and holds a root of any size,
// beyond what one array can. Its bytes are read as a sequence of the pieces (Sequence).
//
// One thread at a time uses a buffer: the task of the connection that owns it.
internal sealed class TransferBuffer(MemoryBudget? budget = null, List<TransferPiece>? suppliedPieces = null) : IBufferWriter<byte>, IDisposable
{
    // The bytes of one piece: as much as GetSpan hands out at once, and more than the largest payload of
    // a block, so that a root payload is written in one piece.
    public const int PieceSize = 64 * 1024;

    private static long PieceBytes =>
        MemoryBudget.ArrayBytes<byte>(PieceSize) +
        MemoryBudget.ObjectBytes(3L * IntPtr.Size + sizeof(long) + 3 * sizeof(int));

    private readonly List<TransferPiece> _pieces = suppliedPieces ?? [];

    public static TransferBuffer ForSnapshot(MemoryLease snapshot)
    {
        var pieces = snapshot.Allocate(MemoryBudget.ObjectBytes(IntPtr.Size + 2L * sizeof(int)), nameof(TransferBuffer),
            static () => new List<TransferPiece>());
        return snapshot.Allocate(MemoryBudget.ObjectBytes(2L * IntPtr.Size + sizeof(long) + sizeof(int)), nameof(TransferBuffer),
            () => new TransferBuffer(snapshot.Budget, pieces), static (_, buffer) => buffer.Dispose());
    }

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

    // Reserve an entire incoming frame before changing what was accepted. Clear resets the write
    // position but retains the previously owned pieces for the next body.
    public void PrepareAppend(int bytes, bool fromStart = false)
    {
        var current = fromStart ? -1 : _current;
        var remaining = current < 0 ? 0 : PieceSize - _pieces[current].Used;
        var more = (int)((Math.Max(0L, (long)bytes - remaining) + PieceSize - 1) / PieceSize);
        var needed = current + 1 + more;
        if (needed > _pieces.Count)
        {
            Grow(needed);
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
        if (_current + 1 == _pieces.Count)
        {
            Grow(_pieces.Count + 1);
        }

        _current++;
    }

    private void Grow(int needed)
    {
        var existing = _pieces.Count;
        if (budget is null)
        {
            while (_pieces.Count < needed)
            {
                _pieces.Add(new TransferPiece());
            }

            return;
        }

        var previous = BudgetCollections.Items(_pieces);
        var grows = needed > previous.Length;
        var length = grows ? BudgetCollections.CapacityFor(previous.Length, needed) : previous.Length;
        var capacity = (needed - existing) * PieceBytes +
            (grows ? MemoryBudget.ArrayBytes<TransferPiece>(length) : 0);
        budget.Reserve(capacity);

        try
        {
            if (grows)
            {
                _pieces.Capacity = length;
                budget.Own(BudgetCollections.Items(_pieces), nameof(TransferBuffer));
            }

            while (_pieces.Count < needed)
            {
                var piece = new TransferPiece();
                _pieces.Add(piece);
                budget.Own(piece, nameof(TransferBuffer));
                budget.Own(piece.Bytes, nameof(TransferBuffer));
            }
        }
        catch
        {
            for (var index = existing; index < _pieces.Count; index++)
            {
                budget.Forget(_pieces[index]);
                budget.Forget(_pieces[index].Bytes);
            }

            _pieces.RemoveRange(existing, _pieces.Count - existing);
            if (!ReferenceEquals(BudgetCollections.Items(_pieces), previous))
            {
                budget.Forget(BudgetCollections.Items(_pieces));
                BudgetCollections.Items(_pieces) = previous;
            }

            budget.Release(capacity);
            throw;
        }

        if (grows)
        {
            budget.Release(previous);
        }
    }

    public void Dispose()
    {
        if (budget is not null)
        {
            foreach (var piece in _pieces)
            {
                budget.Forget(piece.Bytes);
                budget.Release(PieceBytes, piece);
            }

            budget.Release(BudgetCollections.Items(_pieces));
        }

        _pieces.Clear();
        _pieces.Capacity = 0;
        _current = -1;
        Length = 0;
    }
}
