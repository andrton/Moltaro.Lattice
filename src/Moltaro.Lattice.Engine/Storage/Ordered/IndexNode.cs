using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

    // The header, read once: nothing writes it after the node is published.
    private readonly NodeHeader _header;

    // The node of the layout whose payload is given: Arena.Payload of a block of kind IndexNode.
    public IndexNode(IndexLayout layout, Span<byte> payload)
    {
        _layout = layout;
        _payload = payload;
        _header = MemoryMarshal.Read<NodeHeader>(payload);
    }

    // The entries of a leaf, or the separators of an inner node.
    public int Count => _header.Count;

    // 0 for a leaf, the level above its children for an inner node.
    public int Level => _header.Level;

    public bool IsLeaf => Level == 0;

    // The bytes the entries or the separators take, their offsets and the children of an inner node
    // included: what a copy of the node with nothing more or less takes after the header.
    public int UsedBytes => EntriesStart + _header.Bytes - NodeHeader.Size;

    // The room left after the entries or the separators, for the writer, which decides from it whether a
    // copy of the node with one more entry fits or splits.
    public int FreeBytes => _payload.Length - EntriesStart - _header.Bytes;

    // Where the offsets of the entries begin, after the header and the children of an inner node; for an
    // index without a String part, where the entries begin.
    private int OffsetsStart => NodeHeader.Size + (IsLeaf ? 0 : (_header.Count + 1) * IndexLayout.ChildBytes);

    // Where the entries or the separators begin, after the offsets.
    private int EntriesStart => OffsetsStart + (_layout.HasText ? _header.Count * IndexLayout.OffsetBytes : 0);

    // The entry of a leaf, or the separator of an inner node, at the place given, 0 to Count less one: a
    // span over the node, valid while the node is. An entry of fixed width lies at its place in the array of
    // entries; the rest is out of line, so that this small member is inlined into the binary searches of a
    // descent, which call it at every probe (JIT listing at full optimization).
    public ReadOnlySpan<byte> EntryAt(int place) =>
        _layout.HasText ? TextEntryAt(place) : _payload.Slice(EntriesStart + (place * _layout.FixedEntryBytes), _layout.FixedEntryBytes);

    // The entries of a leaf, or the separators of an inner node, from the place first to the place end,
    // as the one block of bytes they lie in: in their order, one after the other, so a range of them is
    // contiguous. A span over the node, valid while the node is; empty for an empty range.
    public ReadOnlySpan<byte> EntriesBetween(int first, int end)
    {
        if (!_layout.HasText)
        {
            return _payload.Slice(EntriesStart + (first * _layout.FixedEntryBytes), (end - first) * _layout.FixedEntryBytes);
        }

        return first == end ? default : _payload[OffsetAt(first)..EndOf(end - 1)];
    }

    // The offset of the entry or separator at the place given from the start of the payload, in a node of
    // an index with a String part.
    public int OffsetAt(int place) => MemoryMarshal.Read<ushort>(_payload[(OffsetsStart + (place * IndexLayout.OffsetBytes))..]);

    // The entry or separator at the place given in a node of an index with a String part, behind its offset.
    private ReadOnlySpan<byte> TextEntryAt(int place) => _payload[OffsetAt(place)..EndOf(place)];

    // Where the entry or separator at the place given ends, in a node of an index with a String part: the
    // entries follow one another in their order, so an entry ends where the next begins, and the last
    // where the bytes of the node end.
    private int EndOf(int place) => place + 1 < _header.Count ? OffsetAt(place + 1) : EntriesStart + _header.Bytes;

    // The child of an inner node at the place given, 0 to Count, by a load with acquire semantics (see
    // the struct).
    public ArenaAddress ChildAt(int place)
    {
        var value = Volatile.Read(ref Child(place));

        return Unsafe.As<ulong, ArenaAddress>(ref value);
    }

    // For the writer, under the lock of the writes: replaces the child at the place by a node it wrote
    // and that holds the same range of entries, by one store with release semantics: the publication of
    // a copy into its live parent (see the struct). The node replaced is retired after the store.
    public void PublishChild(int place, ArenaAddress child) => Volatile.Write(ref Child(place), child.Value);

    // The child address at the place, an aligned word of 8 bytes: the payload of a block begins on a
    // 16-byte boundary and the header takes 8 bytes (NodeHeader).
    private ref ulong Child(int place) =>
        ref Unsafe.As<byte, ulong>(ref _payload[NodeHeader.Size + (place * IndexLayout.ChildBytes)]);
}
