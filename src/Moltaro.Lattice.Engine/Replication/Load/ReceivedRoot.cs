using System.Buffers;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Replication.Load;

// A root a node received that BlockImport.Check found valid, before anything is allocated for it: the
// layout it is written in, the length of its root payload, which the import holds in a buffer of its own,
// and the bytes of the values it holds out of line, in preorder (BlockExport), which lie where the input of
// the check holds them. It is valid until the next check of the same import and while the input is.
internal readonly struct ReceivedRoot
{
    public ReceivedRoot(RootLayout layout, int payloadLength, ReadOnlySequence<byte> outOfLine)
    {
        Layout = layout;
        PayloadLength = payloadLength;
        OutOfLine = outOfLine;
    }

    public RootLayout Layout { get; }

    public int PayloadLength { get; }

    public ReadOnlySequence<byte> OutOfLine { get; }
}
