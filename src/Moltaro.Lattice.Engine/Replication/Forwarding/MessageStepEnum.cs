namespace Moltaro.Lattice.Replication.Forwarding;

// What one frame did to the message it belongs to (MessageAssembler).
internal enum MessageStepEnum
{
    // The message needs more frames.
    Partial = 1,

    // The frame completed the message, whose body is whole.
    Whole = 2,

    // The frame began a message announced above the largest the caller takes, or brought a piece the
    // budget cannot hold: nothing of the message is kept, and its other frames are dropped.
    Refused = 3,

    // The frame was the last of a message refused at an earlier frame.
    Dropped = 4,
}
