namespace Moltaro.Lattice.Replication;

// A body of an announced length, which its frames carry one after another: the first frame begins with a
// header that announces the length and holds the first bytes of the body, every frame after it holds only
// the next bytes, and the last one, and no other, is marked as its end (Protocol.EndsABody). It checks each
// frame before any of it is used: no more bytes than the body still announces, and the flag exactly when
// they complete it. Each failure is a LatticeException with ValidationFailed (Protocol.Invalid), whose
// message names the body by the noun and the id its assembler gives.
//
// The header, the bound on the length it announces and what is done with the bytes are the assembler's
// (Load.RootAssembler, Transactions.TransactionAssembler, Forwarding.MessageAssembler), which holds this
// value in a field and uses it from one thread at a time. It allocates nothing but the message of a
// refusal.
internal struct AnnouncedBody
{
    // The bytes of the body in progress still to arrive; 0 while none is.
    private ulong _remaining;

    // Whether a body is in progress: the next frame continues it, rather than beginning one.
    public readonly bool InProgress => _remaining != 0;

    // Checks the first frame of a body that announces announced bytes, of which the frame holds held, and
    // begins the body when more are to come; whether the frame completes it.
    public bool Begin(in ReceivedFrame frame, ulong announced, ulong held, string noun, ulong? id = null)
    {
        if (held > announced)
        {
            throw Protocol.Invalid($"The first {frame.Kind} frame of {Name(noun, id)} holds {held} bytes of it, more than the {announced} it announces.");
        }

        var complete = held == announced;

        if (complete != frame.EndsABody)
        {
            throw Protocol.Invalid(frame.EndsABody
                ? $"The first {frame.Kind} frame of {Name(noun, id)} is marked as its end and holds {held} of the {announced} bytes it announces."
                : $"The first {frame.Kind} frame of {Name(noun, id)} completes it and is not marked as its end.");
        }

        _remaining = announced - held;

        return complete;
    }

    // Checks the next frame of the body in progress, which holds length bytes of it; whether the frame
    // completes the body.
    public bool Continue(in ReceivedFrame frame, int length, string noun, ulong? id = null)
    {
        if ((ulong)length > _remaining)
        {
            throw Protocol.Invalid($"A {frame.Kind} frame holds {length} bytes of {Name(noun, id)}, which announces {_remaining} more.");
        }

        _remaining -= (ulong)length;

        if ((_remaining == 0) != frame.EndsABody)
        {
            throw Protocol.Invalid(frame.EndsABody
                ? $"A {frame.Kind} frame is marked as the end of {Name(noun, id)}, which announces {_remaining} more bytes."
                : $"A {frame.Kind} frame completes {Name(noun, id)} and is not marked as its end.");
        }

        return _remaining == 0;
    }

    // Forgets the body in progress.
    public void Reset() => _remaining = 0;

    // The body in a refusal: "the transaction 12" with an id, "a root" without one.
    private static string Name(string noun, ulong? id) => id is { } value ? $"the {noun} {value}" : $"a {noun}";
}
