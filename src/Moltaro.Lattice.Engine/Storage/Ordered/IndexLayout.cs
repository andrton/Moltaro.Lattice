using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage.Ordered;

// How the entries and the nodes of one secondary index lie in arena memory (docs/design/ordered-index.md,
// "Nodes in the arena"). Compiled once, when the database opens, from the declaration of the index and
// the root layout of its entity type, and never changed afterwards, so one layout serves the writer and
// every reader at once, as RootLayout does. It knows bytes only; the order of entries is
// Engine.Indexes.IndexOrder's.
//
// An entry, in this order:
// - the identity of the entity: the RootHandle of its root entry, 8 bytes in the byte order of the
//   machine. One root entry contributes one entry to each index, so an entry is unique by its key and
//   its identity, and the entries of one key are a run ordered by identity;
// - the null bitmap: one bit for each part that can be null, NullBitmapBytes bytes;
// - the parts, in the order of the index. A part of a fixed-width type is the bytes its root stores
//   for it, at the width the root gives it (RootLayout); a null part keeps its width and is zero. A
//   String part is one head byte and what the head says: 0 to TextCopyLimit, a copy of that many bytes of
//   the stored text, in the encoding of the database, whatever the storage mode of the field (decision on
//   index structures, "Search-key ownership", the note of 19 November 2026); SharedTextHead, then the
//   length (4 bytes) and the relative address (8) of the text entry the root references for a longer
//   value. A field that is part of an index stores a value longer than TextCopyLimit as a text entry,
//   whatever its storage mode, so a longer value always has one. A null String part is the head 0 with
//   its null bit set.
// An index without a String part has entries of one width, FixedEntryBytes, which lie in an array; an
// entry of an index with one has the width of its values and lies behind an offset. An entry is never
// written after the node that holds it is published.
//
// The node: one block of the arena of kind IndexNode and NodeBytes bytes, its payload NodeHeader, then
// for a leaf the entries, for an inner node the Count + 1 children, relative addresses of 8 bytes on
// 8-byte boundaries, then the Count separators; in a node of an index with a String part the entries or
// the separators follow an array of their offsets from the start of the payload, 2 bytes each, in order.
// Separator i is a copy of an entry: every entry below child i is below it, every entry below child
// i + 1 at or above it. A node holds no fence of its own: the fences of a node are the separators of its
// parent beside it, which a descent passes (ordered-index.md, "Fences").
internal sealed class IndexLayout
{
    // The bytes of a node, header of the block included, unless an index needs more for
    // LeastEntriesPerNode of its widest entries: the size class of 1,024 bytes, the last of the
    // 16-byte steps, so no slack of a class is lost. On the prototype of the structure
    // (src/Prototypes/SecondaryIndexComparison, Ordered, one run of five seconds a case, an Int64 key):
    // an exact search 1.90 M a second on one thread and 7.32 M on sixteen with nodes of 1 KiB, 2.15 M and
    // 8.26 M with nodes of 4 KiB, a difference near the tenth one run does not decide; a write copies one
    // node of each index it changes and retires the old one, so a node of 4 KiB copies and holds retired
    // four times the bytes for each write, beside readers. 18.8, 25.1 and 38.6 bytes an entry for keys of
    // 4, 8 and 16 bytes inserted in random order, at 1 KiB. Measured again in the engine.
    public const int SmallestNodeBytes = 1024;

    // The most bytes of a String value an entry holds a copy of: the most a root holds inline, the same
    // number by the decision on index structures (the note of 19 November 2026), so an indexed value is
    // copied exactly when it could lie in its root, and shared exactly when it lies in a text entry.
    public const int TextCopyLimit = RootLayout.InlineLimit;

    // The head of a String part that references a text entry. The head 254 is kept for a shared key that
    // carries its first bytes beside the reference, which the measurement of ordered-index.md, "Text
    // keys", decides; no other head above TextCopyLimit is written.
    public const byte SharedTextHead = 255;

    // The bytes of a String part that references a text entry: the head, the length, the address.
    public const int SharedTextBytes = 1 + sizeof(uint) + sizeof(ulong);

    // The bytes of the identity that begins every entry.
    public const int IdentityBytes = sizeof(ulong);

    // The bytes of an offset, and of a child address, in a node.
    public const int OffsetBytes = sizeof(ushort);

    public const int ChildBytes = sizeof(ulong);

    // The fewest of its widest entries a leaf holds, and of its widest separators an inner node holds:
    // what makes a split of a full node into two always fit (ordered-index.md, "Writes").
    public const int LeastEntriesPerNode = 4;

    private readonly EntryPart[] _parts;

    private IndexLayout(LatticeIndex index, EntryPart[] parts, int nullBitmapBytes, int fixedEntryBytes, int widestEntryBytes, int nodeBytes, int leastSeparators)
    {
        Index = index;
        _parts = parts;
        NullBitmapBytes = nullBitmapBytes;
        FixedEntryBytes = fixedEntryBytes;
        WidestEntryBytes = widestEntryBytes;
        NodeBytes = nodeBytes;
        LeastSeparators = leastSeparators;
    }

    // The index the layout was compiled for.
    public LatticeIndex Index { get; }

    // The parts of an entry, in the order of the index.
    public ReadOnlySpan<EntryPart> Parts => _parts;

    // The bytes of the null bitmap of an entry: one bit for each part that can be null.
    public int NullBitmapBytes { get; }

    // The bytes of every entry of an index without a String part; 0 for an index with one.
    public int FixedEntryBytes { get; }

    // Whether the index has a String part, and so entries of their own widths, behind offsets, some of
    // which may reference text entries.
    public bool HasText => FixedEntryBytes == 0;

    // The bytes of the widest entry the index can have: every String part a copy of TextCopyLimit bytes.
    public int WidestEntryBytes { get; }

    // The bytes of a node of this index, header of the block included: SmallestNodeBytes, or the
    // smallest size class above it that holds LeastEntriesPerNode of the widest entries in a leaf and of
    // the widest separators, with their children, in an inner node.
    public int NodeBytes { get; }

    // The bytes of the payload of a node: NodeHeader, then the children, the offsets and the entries.
    public int PayloadBytes => NodeBytes - BlockHeader.Size;

    // The fewest separators an inner node holds before it is full: as many of the widest as fit. A root
    // made by a split holds one, and gains at most one for each insert, which bounds how far the tree
    // can grow while writes are prepared (Engine.Indexes.OrderedTree, "The reservation").
    public int LeastSeparators { get; }

    // Compiles the layout of a declared index of the entity type whose root layout is given. The parts
    // come from the root layout: the stored type, width and place of each field. An index whose
    // LeastEntriesPerNode widest entries do not fit the largest block, Arena.MaxBlockSize, is refused with
    // LatticeException, InvalidSchema, naming the index, before any data is admitted, as RootLayout refuses
    // a fixed part beyond one block; about 250 String parts reach it.
    public static IndexLayout Compile(LatticeIndex index, RootLayout root) =>
        throw new NotSupportedException("Secondary indexes are not implemented yet.");

    // The bytes of the entry that starts the span, read from its parts: the identity, the null bitmap,
    // each part at its width, and for a String part its head and what follows it.
    public int EntryLength(ReadOnlySpan<byte> entry) =>
        throw new NotSupportedException("Secondary indexes are not implemented yet.");

    // The identity an entry begins with.
    public static RootHandle IdentityOf(ReadOnlySpan<byte> entry) =>
        throw new NotSupportedException("Secondary indexes are not implemented yet.");

    // Writes the entry a root contributes to the index, under the identity of its root entry, into the
    // destination, which has WidestEntryBytes at least, and returns its bytes. A fixed part is copied from
    // the root as it is stored; a String part of TextCopyLimit bytes or less is copied from wherever its
    // value lies, the root or the text entry of a Dictionary field, and a longer one references the text
    // entry the root's descriptor names. Allocates nothing. For the writer under the lock of the writes,
    // and for a reader inside the protection of reads that checks a root against a key.
    public int WriteEntry(in RootReader root, RootHandle identity, Span<byte> destination) =>
        throw new NotSupportedException("Secondary indexes are not implemented yet.");

    // The text entries the String parts of an entry reference, into the span, which has a place for each
    // String part, and how many there are: for the counts of a node, which holds one reference for each
    // (Text.TextTable; ordered-index.md, "Text references").
    public int SharedTexts(ReadOnlySpan<byte> entry, Span<ArenaAddress> texts) =>
        throw new NotSupportedException("Secondary indexes are not implemented yet.");
}
