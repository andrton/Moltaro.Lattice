using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Replication.Load;

// Puts the roots of a load together on a child from the Blocks frames RootFramer cut them into. Each root
// it completes it hands out whole, as the bytes after its length.
//
// What it checks of a frame, before any root of it is used:
// - While no root is in progress, the frame is a frame of roots: a count of at least one, then for each
//   root its length and its bytes. A root shorter than the envelope of a root is refused. A root that
//   does not end in the frame continues in the next frames; it has to be the only root of its frame,
//   and the frame does not carry the flag of an end. Every other frame of roots ends with its last root
//   exactly — fewer or more bytes than its roots announce are refused — and carries the flag.
// - While a root is in progress, the frame holds only its next bytes: no more than the root still
//   announces, and the flag exactly when they complete it (AnnouncedBody).
// Each failure is a LatticeException with ValidationFailed. A root announced larger than the memory the
// child may take for its database cannot be held by it, whatever arrives after: that ends the load at
// once, with Unavailable and the reason, before anything is taken for the root.
//
// A small root is handed out where it lies, in the payload of its frame, so it is valid only until the
// next frame is received. A root of several frames is collected in a TransferBuffer, which takes the
// pieces the root needs as its bytes arrive and keeps them for the next large root: a child holds one
// root at a time outside its arena.
internal sealed class RootAssembler : IDisposable
{
    // The type id, the last mutation sequence and the layout version that begin every root.
    public const int EnvelopeSize = RootEnvelope.Size + sizeof(uint);

    private readonly TransferBuffer _large;
    private readonly MemoryBudget? _budget;

    // The root in progress, whose bytes are still to arrive.
    private AnnouncedBody _root;

    // The most bytes one root may announce: the memory the child may take for its database.
    private readonly long _largestRoot;

    public RootAssembler(long largestRoot, MemoryBudget? budget = null)
    {
        _largestRoot = largestRoot;
        _budget = budget;
        _large = new TransferBuffer(budget);
    }

    // Whether a root is in progress: at the end of a load none may be.
    public bool InProgress => _root.InProgress;

    // Reads one Blocks frame and adds the roots it completes to the list, in their order (see the
    // class). The list is the caller's and is not cleared here.
    public void Accept(in ReceivedFrame frame, List<ReadOnlySequence<byte>> completed)
    {
        var payload = frame.Payload;

        if (_root.InProgress)
        {
            Continue(frame, completed);

            return;
        }

        if (payload.Length < sizeof(uint))
        {
            throw Protocol.Invalid($"A Blocks frame of {payload.Length} bytes holds no count of roots.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);
        var offset = sizeof(uint);

        if (count == 0)
        {
            throw Protocol.Invalid("A Blocks frame holds no root.");
        }

        for (var index = 0u; index < count; index++)
        {
            if (payload.Length - offset < sizeof(ulong))
            {
                throw Protocol.Invalid($"A Blocks frame announces {count} roots and ends before the length of root {index + 1}.");
            }

            var length = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span[offset..]);

            offset += sizeof(ulong);

            if (length < EnvelopeSize)
            {
                throw Protocol.Invalid($"A root announces {length} bytes, fewer than the {EnvelopeSize} of the envelope of a root.");
            }

            if (length > (ulong)_largestRoot)
            {
                throw new LatticeException(LatticeErrorEnum.Unavailable, $"The master sends a root of {length} bytes, more than the memory limit of this child, {_largestRoot} bytes, can hold: the child cannot hold the database of its master.");
            }

            var available = (ulong)(payload.Length - offset);

            if (available >= length)
            {
                completed.EnsureCapacity(completed.Count + 1, _budget, nameof(RootAssembler));
                completed.Add(new ReadOnlySequence<byte>(payload.Slice(offset, (int)length)));
                offset += (int)length;

                continue;
            }

            if (count != 1)
            {
                throw Protocol.Invalid($"A Blocks frame ends {length - available} bytes before the end of a root it announces.");
            }

            // The root does not end in this frame, which therefore may not be marked as its end.
            _root.Begin(frame, length, available, "root");
            _large.Clear();
            _large.Write(payload.Span[offset..]);

            return;
        }

        if (offset != payload.Length)
        {
            throw Protocol.Invalid($"A Blocks frame holds {payload.Length - offset} bytes after the {count} roots it announces.");
        }

        if (!frame.EndsABody)
        {
            throw Protocol.Invalid("A Blocks frame whose roots all end in it is not marked as their end.");
        }
    }

    // The next bytes of the root in progress.
    private void Continue(in ReceivedFrame frame, List<ReadOnlySequence<byte>> completed)
    {
        var payload = frame.Payload.Span;
        var complete = _root.Continue(frame, payload.Length, "root");

        _large.Write(payload);

        if (complete)
        {
            completed.EnsureCapacity(completed.Count + 1, _budget, nameof(RootAssembler));
            completed.Add(_large.Sequence);
        }
    }

    public void Dispose() => _large.Dispose();
}
