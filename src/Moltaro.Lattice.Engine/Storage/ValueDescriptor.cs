using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage;

// The 16 bytes that stand in the fixed part of a root for one String or Binary value. The fields,
// their widths and their order are those of the milestone contract ("Native layout and
// allocation"): kind and flags (4), length or count (4), relative address (8), for a Dictionary field
// the address of the shared entry; a test asserts the size. What Length and Location mean depends on
// the kind; DescriptorKindEnum says it for each.
//
// Sequential layout, because the declared order is the order of the bytes in the root block. No
// Pack: in this order every field already sits on its natural boundary, so natural alignment adds
// no padding.
[StructLayout(LayoutKind.Sequential)]
internal struct ValueDescriptor
{
    // The kind of the descriptor, and with it the meaning of the two fields after it. The milestone
    // contract gives these four bytes to kind and flags; DescriptorKindEnum defines the kinds, and no
    // flag is defined yet.
    public DescriptorKindEnum Kind;

    // The bytes of the value in its stored encoding; for a collection, the number of its elements.
    public uint Length;

    // Where the value is: a relative address of its blocks or of the entry of its text, or an offset
    // inside the root block, by the kind.
    public ulong Location;
}
