using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage;

// The descriptors of a root, which name everything it holds outside its root block: one for each String
// and Binary field, in the order of the layout, 16 bytes each, closing the fixed part
// (RootLayout.DescriptorOffset). It walks the payload of a root block, or of anything laid out as one — the
// image of a writer, the copy of a root on the wire, a root received — so that the free, the publication,
// the retirement and the byte counts of a root (RootStorage, RootWriter, Engine.Writes.WriteExecutor), its
// export (Replication.Load.BlockExport) and its import (Replication.Load.BlockImport) walk it the same way.
// Each step gives the descriptor (Current), the bytes it lies in (Place), its offset in the root block
// (Offset), its field (Field) and its position (Index). A ref struct over the span, for foreach and for an
// explicit loop: it allocates nothing and calls nothing virtual, and it reads the payload only.
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

    // The String or Binary field of the descriptor.
    public readonly ref readonly StoredField Field => ref _layout.DescriptorField(_index);

    // The position of the descriptor among those of the root, from 0; -1 before the first.
    public readonly int Index => _index;

    public readonly RootDescriptors GetEnumerator() => this;

    public bool MoveNext() => ++_index < _layout.DescriptorCount;
}
