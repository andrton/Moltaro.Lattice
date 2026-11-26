using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage;

// The decoded descriptor of a root or record value. What Length and Location mean depends
// on Kind. See DescriptorKindEnum and docs/design/arena-protocol.md.
//
// The sequential layout is also the canonical scratch and transfer representation, used by
// ValueBlocks: kind (4), length or count (4), location (8). In a native slot ResidentDescriptor
// places the location at its aligned word instead.
[StructLayout(LayoutKind.Sequential)]
internal struct ValueDescriptor
{
    // The kind of the descriptor, and with it the meaning of the two fields after it. The milestone
    // contract gives these four bytes to kind and flags; DescriptorKindEnum defines the kinds, and no
    // flag is defined yet.
    public DescriptorKindEnum Kind;

    // The bytes of the value in its stored encoding, those of the records of a List whose elements hold
    // no descriptor included; for a List of kind Records, the number of its elements.
    public uint Length;

    // Where the value is: a relative address of its blocks or of the entry of its text, or an offset
    // inside the root block, by the kind.
    public ulong Location;
}
