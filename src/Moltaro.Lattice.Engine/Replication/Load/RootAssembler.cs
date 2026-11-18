using System.Buffers;
using System.Buffers.Binary;

namespace Moltaro.Lattice.Replication.Load;

// Puts the roots of a load together on a child from the Blocks frames RootFramer cut them into (the
// planner's block of 15 November 2026, "A root larger than a frame", and his decision of 17 November
// after the study, point 3). Each root it completes it hands out whole, as the bytes after its length.
//
// What it checks of a frame, before any root of it is used:
// - While no root is in progress, the frame is a frame of roots: a count of at least one, then for each
//   root its length and its bytes. A root shorter than the envelope of a root is refused. A root that
//   does not end in the frame continues in the next frames; it has to be the only root of its frame,
//   and the frame does not carry the flag of an end. Every other frame of roots ends with its last root
//   exactly — fewer or more bytes than its roots announce are refused — and carries the flag.
// - While a root is in progress, the frame holds only its next bytes: no more than the root still
//   announces, and the flag exactly when they complete it.
// Each failure is a LatticeException with ValidationFailed. A root announced larger than the memory the
// child may take for its database cannot be held by it, whatever arrives after: that ends the load at
// once, with Unavailable and the reason, before anything is taken for the root.
//
// A small root is handed out where it lies, in the payload of its frame, so it is valid only until the
// next frame is received. A root of several frames is collected in a TransferBuffer, which takes the
// pieces the root needs as its bytes arrive and keeps them for the next large root: a child holds one
// root at a time outside its arena.
internal sealed class RootAssembler
{
    // The type id, the last mutation sequence and the layout version that begin every root.
    public const int EnvelopeSize = RootEnvelope.Size + sizeof(uint);

    private readonly TransferBuffer _large = new();

    // The bytes of the root in progress still to arrive; 0 while none is.
    private ulong _remaining;

    // The most bytes one root may announce: the memory the child may take for its database.
    private readonly long _largestRoot;

    public RootAssembler(long largestRoot)
    {
        _largestRoot = largestRoot;
    }

    // Whether a root is in progress: at the end of a load none may be.
    public bool InProgress => _remaining != 0;

    // Reads one Blocks frame and adds the roots it completes to the list, in their order (see the
    // class). The list is the caller's and is not cleared here.
    public void Accept(in ReceivedFrame frame, List<ReadOnlySequence<byte>> completed)
    {
        var payload = frame.Payload;

        if (_remaining != 0)
        {
            Continue(frame, completed);

            return;
        }

        if (payload.Length < sizeof(uint))
        {
            throw Invalid($"A Blocks frame of {payload.Length} bytes holds no count of roots.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);
        var offset = sizeof(uint);

        if (count == 0)
        {
            throw Invalid("A Blocks frame holds no root.");
        }

        for (var index = 0u; index < count; index++)
        {
            if (payload.Length - offset < sizeof(ulong))
            {
                throw Invalid($"A Blocks frame announces {count} roots and ends before the length of root {index + 1}.");
            }

            var length = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span[offset..]);

            offset += sizeof(ulong);

            if (length < EnvelopeSize)
            {
                throw Invalid($"A root announces {length} bytes, fewer than the {EnvelopeSize} of the envelope of a root.");
            }

            if (length > (ulong)_largestRoot)
            {
                throw new LatticeException(LatticeErrorEnum.Unavailable, $"The master sends a root of {length} bytes, more than the memory limit of this child, {_largestRoot} bytes, can hold: the child cannot hold the database of its master.");
            }

            var available = (ulong)(payload.Length - offset);

            if (available >= length)
            {
                completed.Add(new ReadOnlySequence<byte>(payload.Slice(offset, (int)length)));
                offset += (int)length;

                continue;
            }

            if (count != 1 || frame.EndsABody)
            {
                throw Invalid($"A Blocks frame ends {length - available} bytes before the end of a root it announces.");
            }

            _large.Clear();
            _large.Write(payload.Span[offset..]);
            _remaining = length - available;

            return;
        }

        if (offset != payload.Length)
        {
            throw Invalid($"A Blocks frame holds {payload.Length - offset} bytes after the {count} roots it announces.");
        }

        if (!frame.EndsABody)
        {
            throw Invalid("A Blocks frame whose roots all end in it is not marked as their end.");
        }
    }

    private static LatticeException Invalid(string message) => new(LatticeErrorEnum.ValidationFailed, message);

    // The next bytes of the root in progress.
    private void Continue(in ReceivedFrame frame, List<ReadOnlySequence<byte>> completed)
    {
        var payload = frame.Payload.Span;

        if ((ulong)payload.Length > _remaining)
        {
            throw Invalid($"A Blocks frame holds {payload.Length} bytes of a root that announces {_remaining} more.");
        }

        _large.Write(payload);
        _remaining -= (ulong)payload.Length;

        if ((_remaining == 0) != frame.EndsABody)
        {
            throw Invalid(frame.EndsABody
                ? $"A Blocks frame is marked as the end of a root that announces {_remaining} more bytes."
                : "A Blocks frame completes a root and is not marked as its end.");
        }

        if (_remaining == 0)
        {
            completed.Add(_large.Sequence);
        }
    }
}
