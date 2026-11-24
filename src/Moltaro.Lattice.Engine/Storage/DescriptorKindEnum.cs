namespace Moltaro.Lattice.Storage;

// Where a variable value of a root or record lies, and how to interpret its descriptor's Length and
// Location. See docs/design/arena-protocol.md, "Stored structures and root transfer."
internal enum DescriptorKindEnum : uint
{
    // A null value has a zero descriptor and a set null bit. During placement the same kind with
    // a nonzero length is pending and names no storage (ValueBlocks).
    Null = 0,

    // The value lies after the fixed part of a root or after the records in their block. Length is
    // its encoded byte count. Location is relative to that block's start, including its header.
    // An empty value is inline with length 0.
    Inline = 1,

    // The value lies whole in a Value block whose parent is the block containing the descriptor.
    // Length is its encoded bytes; Location is the relative address of that block, as
    // ArenaAddress.Value gives it. In a chunk directory every descriptor has this kind and names one
    // chunk: Length is the bytes of the value that chunk holds, Location its relative address.
    Block = 2,

    // The value lies in chunks, blocks of kind Chunk, in the order of its bytes. Length is its
    // encoded bytes; Location is the relative address of its chunk directory, one block of kind
    // ChunkDirectory whose parent contains this descriptor and which holds one 16-byte descriptor for
    // each chunk. The parent of a chunk is its directory.
    Chunked = 3,

    // The value is a Dictionary text or a long text used in a secondary index, as
    // StoredField.LiesInTextEntry decides. Length is its nonzero encoded byte count. Location
    // is the relative address of the entry's block of kind SharedText or its directory of chunks.
    // The descriptor holds one reference to the entry until its block is freed (Text.TextTable).
    Text = 4,

    // A List whose elements hold descriptors, including through an owned value. Length gives
    // the element count. Location gives the relative address of its block of records or directory.
    // An empty List has location zero. Each record lies whole in one block.
    Records = 5,

    // A Map or Set with a derived hash table. Length gives the number of entries and Location
    // names the lookup block. The first descriptor of that block names the entries.
    Lookup = 6,
}
