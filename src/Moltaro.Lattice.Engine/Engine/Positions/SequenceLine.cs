using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Engine.Positions;

// The sequence of the last published position, alone on its cache lines (PositionClock). The thread
// that publishes writes it at every write and reads load it, so nothing that a read loads, or that
// the publishing thread writes, may share its line: a store to a line other processors hold makes each
// of them load the line again. The value lies 128 bytes into the struct and the struct ends 120 bytes
// after it, so that it does not share the pair of lines a processor fetches together either. 256 bytes.
[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct SequenceLine
{
    [FieldOffset(128)]
    public ulong Value;
}
