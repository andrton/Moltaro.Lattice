using System.Buffers;
using System.Buffers.Binary;

namespace Moltaro.Lattice.Replication.Load;

// Cuts the roots of a load into Blocks frames and sends them (the planner's block of 15 November 2026,
// "A root larger than a frame", and his decision of 17 November after the study, point 3). The bodies
// RootAssembler reads on the child:
// - A frame of whole roots: their count, a UInt32, then for each root its length, a UInt64, and its
//   bytes: the type id and the last mutation sequence of the capture, then what BlockExport writes.
//   Every root ends in the frame, so the frame carries Protocol.EndsARoot. Small roots share a frame:
//   a root goes into the frame being filled while it fits there.
// - A root that does not fit an empty frame travels in frames of its own: the first holds the count 1,
//   the length of the root and its first bytes, without the flag; each next frame holds only its next
//   bytes, and the last of them carries the flag. The first frame announces the whole length, so the
//   child takes what the root needs once and checks its end.
// Counts and lengths are little-endian.
//
// A root waits in the buffer its capture filled (TransferBuffer) while its frames are sent, and each
// frame is sent before the next is made, so the sender holds one root and one frame at a time outside
// the arena. The frame being filled is sent when the next root does not fit it, and by FlushAsync.
internal sealed class RootFramer
{
    private const int CountSize = sizeof(uint);

    private const int LengthSize = sizeof(ulong);

    private readonly FrameConnection _connection;

    // The payload of the frame being filled; reused for every frame.
    private readonly byte[] _payload = new byte[Protocol.MaxDataPayload];

    // Its bytes in use, the count included, and its roots; 0 and 0 while no frame is being filled.
    private int _used;

    private uint _count;

    public RootFramer(FrameConnection connection)
    {
        _connection = connection;
    }

    // The roots added so far: the count LoadEnd announces.
    public ulong Roots { get; private set; }

    // Adds the root whose bytes the buffer holds, sending every frame that is complete.
    public async ValueTask AddAsync(TransferBuffer root, CancellationToken cancellationToken)
    {
        var bytes = root.Sequence;

        if (_count > 0 && _used + LengthSize + bytes.Length > _payload.Length)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (CountSize + LengthSize + bytes.Length <= _payload.Length)
        {
            if (_count == 0)
            {
                _used = CountSize;
            }

            BinaryPrimitives.WriteUInt64LittleEndian(_payload.AsSpan(_used), (ulong)bytes.Length);
            _used += LengthSize;
            bytes.CopyTo(_payload.AsSpan(_used));
            _used += (int)bytes.Length;
            _count++;
        }
        else
        {
            await SendAloneAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        Roots++;
    }

    // Sends the frame being filled, if there is one.
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (_count == 0)
        {
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(_payload, _count);
        _connection.Add(FrameKindEnum.Blocks, Protocol.EndsARoot, _payload.AsSpan(0, _used));
        _count = 0;
        _used = 0;

        await _connection.SendAsync(cancellationToken).ConfigureAwait(false);
    }

    // A root larger than a frame, in frames of its own (see the class); no frame is being filled.
    private async ValueTask SendAloneAsync(ReadOnlySequence<byte> bytes, CancellationToken cancellationToken)
    {
        var first = _payload.Length - CountSize - LengthSize;

        BinaryPrimitives.WriteUInt32LittleEndian(_payload, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(_payload.AsSpan(CountSize), (ulong)bytes.Length);
        bytes.Slice(0, first).CopyTo(_payload.AsSpan(CountSize + LengthSize));
        _connection.Add(FrameKindEnum.Blocks, 0, _payload);
        await _connection.SendAsync(cancellationToken).ConfigureAwait(false);

        bytes = bytes.Slice(first);

        while (bytes.Length > 0)
        {
            var part = (int)Math.Min(bytes.Length, _payload.Length);

            bytes.Slice(0, part).CopyTo(_payload);
            bytes = bytes.Slice(part);
            _connection.Add(FrameKindEnum.Blocks, bytes.Length == 0 ? Protocol.EndsARoot : 0, _payload.AsSpan(0, part));
            await _connection.SendAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
