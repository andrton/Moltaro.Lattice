using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Engine.Protection;

// One slot of the read gate, which one read holds at a time (ReadGate). Its sequence is even while
// the slot is free and odd while a read holds it; it only grows, by one when a read takes the slot
// and by one when that read leaves it, so a value that a writer saw odd and later sees changed tells
// that the read it saw has left.
//
// 128 bytes with the sequence in the middle, so that no two sequences, and no sequence and the
// header of the array, share a cache line or the pair of lines that the processors of x64 fetch
// together: a read writes only the line of its own slot, which other reads do not write.
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct ReaderSlot
{
    [FieldOffset(64)]
    public long Sequence;
}
