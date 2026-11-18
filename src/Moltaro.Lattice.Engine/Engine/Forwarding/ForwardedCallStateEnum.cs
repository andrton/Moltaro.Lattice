namespace Moltaro.Lattice.Engine.Forwarding;

// Where a write forwarded through a child stands on its way to the master (ForwardedCall).
internal enum ForwardedCallStateEnum
{
    // In the queue of the sender; its token withdraws it.
    Queued = 1,

    // Withdrawn by its token before the sender took it: never sent.
    Withdrawn = 2,

    // Taken by the sender, its first frame begun: the token no longer withdraws it.
    Taken = 3,

    // Its last frame accepted by the socket: the master may execute it.
    Sent = 4,

    // Ended without being sent whole, when the connection ended: not executed.
    Ended = 5,
}
