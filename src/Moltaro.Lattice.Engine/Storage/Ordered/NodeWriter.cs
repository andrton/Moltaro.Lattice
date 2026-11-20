using System.Runtime.InteropServices;

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

    private readonly int _level;

    private readonly int _count;

    // Where the offsets begin, after the header and the children, and where the entries begin, after
    // the offsets.
    private readonly int _offsets;

    private readonly int _entries;

    // The entries and the children written so far, and where the next entry goes.
    private int _added;

    private int _children;

    private int _next;

    // A writer of a node of the level given, 0 for a leaf, that will hold the count of entries or of
    // separators given, into the payload of a prepared block of kind IndexNode of the layout.
    public NodeWriter(IndexLayout layout, Span<byte> payload, int level, int count)
    {
        _layout = layout;
        _payload = payload;
        _level = level;
        _count = count;
        _offsets = NodeHeader.Size + (level == 0 ? 0 : (count + 1) * IndexLayout.ChildBytes);
        _entries = _offsets + (layout.HasText ? count * IndexLayout.OffsetBytes : 0);
        _next = _entries;
    }

    // Writes the next entry of a leaf, or the next separator of an inner node, in their order: a copy
    // of the bytes given, which IndexLayout laid out.
    public void Add(ReadOnlySpan<byte> entry)
    {
        if (_layout.HasText)
        {
            MemoryMarshal.Write(_payload[(_offsets + (_added * IndexLayout.OffsetBytes))..], (ushort)_next);
        }

        entry.CopyTo(_payload[_next..]);
        _next += entry.Length;
        _added++;
    }

    // Writes the entries or separators of the node given from the place first to the place end as the
    // next ones, in their order, as one block: the bytes they lie in (IndexNode.EntriesBetween), copied at
    // once, and for entries behind offsets their offsets, each moved by the one distance between where
    // the block lay and where it goes. The same bytes and offsets as Add of each of them.
    public void AddRange(scoped in IndexNode node, int first, int end)
    {
        var block = node.EntriesBetween(first, end);

        if (_layout.HasText)
        {
            var shift = first == end ? 0 : _next - node.OffsetAt(first);

            for (var place = first; place < end; place++)
            {
                MemoryMarshal.Write(_payload[(_offsets + (_added * IndexLayout.OffsetBytes))..], (ushort)(node.OffsetAt(place) + shift));
                _added++;
            }
        }
        else
        {
            _added += end - first;
        }

        block.CopyTo(_payload[_next..]);
        _next += block.Length;
    }

    // Writes the next child of an inner node, in their order: count + 1 of them.
    public void AddChild(ArenaAddress child)
    {
        MemoryMarshal.Write(_payload[(NodeHeader.Size + (_children * IndexLayout.ChildBytes))..], child.Value);
        _children++;
    }

    // Writes the header, once every entry and child is written; the node is then ready to be published.
    // InvalidOperationException when fewer entries or children were written than the count promised.
    public readonly void Complete()
    {
        if (_added != _count || _children != (_level == 0 ? 0 : _count + 1))
        {
            throw new InvalidOperationException($"A node of {_count} entries at level {_level} was written with {_added} entries and {_children} children.");
        }

        var header = new NodeHeader { Count = (ushort)_count, Level = (byte)_level, Bytes = (ushort)(_next - _entries) };

        MemoryMarshal.Write(_payload, in header);
    }
}
