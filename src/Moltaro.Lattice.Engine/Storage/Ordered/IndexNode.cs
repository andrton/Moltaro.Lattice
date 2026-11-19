namespace Moltaro.Lattice.Storage.Ordered;

// One node of an ordered index read in place, over the payload of its block, as IndexLayout lays it out:
// its header, its entries or separators by their place, and the children of an inner node.
//
// What is written after a node is published, and how it is read (docs/design/ordered-index.md, "How a
// reader descends"). Nothing of a published node is written again but the child addresses of an inner
// node, each of which the writer replaces by one store of 8 bytes with release semantics (PublishChild),
// on its 8-byte boundary, so that a reader sees the old address or the new one whole. A reader loads a
// child address with acquire semantics (ChildAt), which pairs with the release of the store that
// published the address or the node above it: everything the writer wrote into the node behind it, its
// header, its entries and its children, was written before that store, so the plain loads the reader
// makes of it after the acquire see them, on x64 and on ARM64 alike. The header, the offsets and the
// entries are read with plain loads, since nothing writes them after the node is published.
//
// Made inside the protection of reads, or by the writer under the lock of the writes, over a block that
// is live or retired; the span does not outlive that hold, and the node is never freed while a reader
// that reached it is inside (Engine.Protection.RetiredMemory). A readonly ref struct, so it lives on the
// stack of the step that reads it and can be kept nowhere else.
internal readonly ref struct IndexNode
{
    private readonly IndexLayout _layout;

    // The payload of the node's block: what IndexLayout lays out after the BlockHeader.
    private readonly Span<byte> _payload;

    // The node of the layout whose payload is given: Arena.Payload of a block of kind IndexNode.
    public IndexNode(IndexLayout layout, Span<byte> payload)
    {
        _layout = layout;
        _payload = payload;

        throw new NotSupportedException("Secondary indexes are not implemented yet.");
    }

    // The entries of a leaf, or the separators of an inner node.
    public int Count => throw new NotSupportedException("Secondary indexes are not implemented yet.");

    // 0 for a leaf, the level above its children for an inner node.
    public int Level => throw new NotSupportedException("Secondary indexes are not implemented yet.");

    public bool IsLeaf => Level == 0;

    // The room left after the entries or the separators, for the writer, which decides from it whether a
    // copy of the node with one more entry fits or splits.
    public int FreeBytes => throw new NotSupportedException("Secondary indexes are not implemented yet.");

    // The entry of a leaf, or the separator of an inner node, at the place given, 0 to Count less one: a
    // span over the node, valid while the node is.
    public ReadOnlySpan<byte> EntryAt(int place) =>
        throw new NotSupportedException("Secondary indexes are not implemented yet.");

    // The child of an inner node at the place given, 0 to Count, by a load with acquire semantics (see
    // the struct).
    public ArenaAddress ChildAt(int place) =>
        throw new NotSupportedException("Secondary indexes are not implemented yet.");

    // For the writer, under the lock of the writes: replaces the child at the place by a node it wrote
    // and that holds the same range of entries, by one store with release semantics: the publication of
    // a copy into its live parent (see the struct). The node replaced is retired after the store.
    public void PublishChild(int place, ArenaAddress child) =>
        throw new NotSupportedException("Secondary indexes are not implemented yet.");
}
