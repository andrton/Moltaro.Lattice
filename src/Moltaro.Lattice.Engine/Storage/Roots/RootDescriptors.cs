using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// The consecutive descriptors that close the fixed part of a root payload (RootLayout.DescriptorOffset).
// RootWriter uses the sequence to adopt blocks when completing a root. BlockExport uses it to walk
// the root's descriptors in their layout order and to zero addresses in the copy it sends. Ownership
// operations and walks into the records of Lists use StructureWalk.
//
// A ref struct over a caller-owned span. Each step exposes the descriptor and the bytes it occupies,
// along with its block-relative offset and layout position. The loop allocates nothing.
internal ref struct RootDescriptors
{
    private readonly RootLayout _layout;

    // The payload of the root block, without its header.
    private readonly Span<byte> _payload;

    private int _index;

    // The descriptors of the root of the layout whose payload is given, before the first.
    public RootDescriptors(RootLayout layout, Span<byte> payload)
    {
        _layout = layout;
        _payload = payload;
        _index = -1;
    }

    // The descriptor at the place.
    public readonly ValueDescriptor Current => ValueBlocks.Read(Place);

    // The 16 bytes the descriptor lies in, in the payload.
    public readonly Span<byte> Place => _payload.Slice(Offset - BlockHeader.Size, ValueBlocks.DescriptorSize);

    // The offset of the descriptor in the root block, its header included, as the header of a block the
    // descriptor names records it (BlockHeader.DescriptorOffset).
    public readonly int Offset => _layout.DescriptorOffset + (_index * ValueBlocks.DescriptorSize);

    // The place of the descriptor: a String, Binary or List field, of the entity or of one of its owned values.
    public readonly ref readonly StoredField Field => ref _layout.DescriptorField(_index);

    // The position of the descriptor among those of the root, from 0; -1 before the first.
    public readonly int Index => _index;

    public readonly RootDescriptors GetEnumerator() => this;

    public bool MoveNext() => ++_index < _layout.DescriptorCount;
}
