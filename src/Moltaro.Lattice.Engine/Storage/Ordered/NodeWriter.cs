namespace Moltaro.Lattice.Storage.Ordered;

// Writes one node of an ordered index into the payload of a prepared block that no reader can reach
// yet, as IndexLayout lays it out: for the writer, under the lock of the writes, which takes the block
// from the nodes it reserved before the commit (Engine.Indexes.OrderedTree) and publishes it once it is
// written. The writer knows the count when it starts: a node is always the copy of a node with one
// entry more or less, or one part of a split or a merge, so the offsets and the entries are written in
// their order, entries after the offsets, and the header last (Complete).
//
// Every byte a reader reads is written here, with plain stores, before the store that publishes the
// node (IndexNode.PublishChild, or the root of the tree), whose release orders them before it. Nothing
// here allocates, and nothing fails for a node that the layout and the count fit: the caller has
// decided from the bytes of the entries where a node splits.
internal ref struct NodeWriter
{
    private readonly IndexLayout _layout;

    private readonly Span<byte> _payload;

    // A writer of a node of the level given, 0 for a leaf, that will hold the count of entries or of
    // separators given, into the payload of a prepared block of kind IndexNode of the layout.
    public NodeWriter(IndexLayout layout, Span<byte> payload, int level, int count)
    {
        _layout = layout;
        _payload = payload;

        throw new NotSupportedException("Secondary indexes are not implemented yet.");
    }

    // Writes the next entry of a leaf, or the next separator of an inner node, in their order: a copy
    // of the bytes given, which IndexLayout laid out.
    public void Add(ReadOnlySpan<byte> entry) =>
        throw new NotSupportedException("Secondary indexes are not implemented yet.");

    // Writes the next child of an inner node, in their order: count + 1 of them.
    public void AddChild(ArenaAddress child) =>
        throw new NotSupportedException("Secondary indexes are not implemented yet.");

    // Writes the header, once every entry and child is written; the node is then ready to be published.
    // InvalidOperationException when fewer entries or children were written than the count promised.
    public void Complete() =>
        throw new NotSupportedException("Secondary indexes are not implemented yet.");
}
