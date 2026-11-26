using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Budgeting;

namespace Moltaro.Lattice.Replication.Forwarding;

// The bytes of one forwarded write or of one reply as they are sent: the announced length, the length of
// the body (UInt64), then the body, written through this IBufferWriter and cut into the payloads of its
// frames as it is written (ForwardEncoding; brief of Epic 1, sprint S12, the planner's decision after the
// study, points 3 and 6). Each piece the writer holds is the payload of one frame, at most
// Protocol.MaxDataPayload bytes, the first beginning with the announced length, which Complete writes; a
// request of any size is held in pieces of a frame each, so that nothing of it lies in the large object
// heap, and a small one takes one small array, which grows while it is written.
//
// A span asked for is given whole in one piece: when the piece being written cannot give it, that piece
// ends shorter than a frame and the next one begins. A receiver takes any cut (Protocol, MessageAssembler).
//
// One thread at a time writes a message; once it is complete, the sender of the connection reads its
// pieces, and nothing writes it any more.
internal sealed class OutgoingMessage : IBufferWriter<byte>, IDisposable
{
    // The first capacity of a piece, enough for the body of a small write without growing.
    private const int FirstCapacity = 512;

    private readonly List<byte[]> _pieces = [];

    private readonly List<int> _lengths = [];

    private readonly MemoryBudget? _budget;
    private byte[] _current;

    private int _used;

    public OutgoingMessage(MemoryBudget? budget = null)
    {
        _budget = budget;
        _current = NewPiece(FirstCapacity);
        _used = sizeof(ulong);
    }

    // The bytes of the body written so far.
    public long BodyLength { get; private set; }

    // The frames of the message, once it is complete.
    public int FrameCount => _pieces.Count;

    // The payload of a frame of the message, once it is complete.
    public ReadOnlySpan<byte> Frame(int index) => _pieces[index].AsSpan(0, _lengths[index]);

    // Ends the body: its length goes into the first frame. Nothing is written afterwards.
    public void Complete()
    {
        SealPiece();
        BinaryPrimitives.WriteUInt64LittleEndian(_pieces[0], (ulong)BodyLength);
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sizeHint, Protocol.MaxDataPayload);

        var wanted = Math.Max(sizeHint, 1);

        if (_current.Length - _used < wanted)
        {
            if (_used + wanted <= Protocol.MaxDataPayload)
            {
                // The piece grows, up to a frame's worth.
                var grown = NewPiece(Math.Min(Protocol.MaxDataPayload, Math.Max(2 * _current.Length, _used + wanted)));

                _current.AsSpan(0, _used).CopyTo(grown);
                _budget?.Release(_current);
                _current = grown;
            }
            else
            {
                SealPiece();
                _current = NewPiece(Protocol.MaxDataPayload);
                _used = 0;
            }
        }

        return _current.AsMemory(_used);
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _current.Length - _used);

        _used += count;
        BodyLength += count;
    }

    // Copies the bytes into the body, across as many pieces as they need.
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

    private byte[] NewPiece(int length) => _budget?.Allocate<byte>(length, nameof(OutgoingMessage)) ?? new byte[length];

    private void SealPiece()
    {
        var count = _pieces.Count + 1;
        _pieces.EnsureCapacity(count, _budget, nameof(OutgoingMessage));
        _lengths.EnsureCapacity(count, _budget, nameof(OutgoingMessage));
        _pieces.Add(_current);
        _lengths.Add(_used);
        _current = [];
    }

    public void Dispose()
    {
        if (_budget is { } budget)
        {
            budget.Release(_current);
            foreach (var piece in _pieces)
            {
                budget.Release(piece);
            }

            budget.Release(BudgetCollections.Items(_pieces));
            budget.Release(BudgetCollections.Items(_lengths));
        }

        _current = [];
        _pieces.Clear();
        _pieces.Capacity = 0;
        _lengths.Clear();
        _lengths.Capacity = 0;
    }

}
