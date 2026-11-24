namespace Moltaro.Lattice.Storage;

// Where a variable value of a root lies, as the Kind of its ValueDescriptor says, and with it what
// the Length and the Location of the descriptor mean (milestone contract, "Native layout and
// allocation"). RootLayout says which value takes which kind.
internal enum DescriptorKindEnum : uint
{
    // The value is null, and the nullable bitmap of the root says so too. The descriptor of a null
    // value is all zero.
    Null = 0,

    // The value lies in the root block, after its fixed part. Length is its encoded bytes; Location
    // is its offset from the start of the root block, the block header included, as
    // RootLayout.FixedSize counts. An empty value is inline with length 0.
    Inline = 1,

    // The value lies whole in one block of its own, of kind Value, whose parent is the root block.
    // Length is its encoded bytes; Location is the relative address of that block, as
    // ArenaAddress.Value gives it. In a chunk directory every descriptor has this kind and names one
    // chunk: Length is the bytes of the value that chunk holds, Location its relative address.
    Block = 2,

    // The value lies in chunks, blocks of kind Chunk, in the order of its bytes. Length is its
    // encoded bytes; Location is the relative address of its chunk directory, one block of kind
    // ChunkDirectory whose parent is the root block and which holds one 16-byte descriptor for
    // each chunk. The parent of a chunk is its directory.
    Chunked = 3,

    // The value is the text of a field with Dictionary storage, which an entry of the text table of the
    // database holds, not the root (brief of Epic 1, sprint S13, the planner's decision after the study,
    // points 1 and 2). Length is its encoded bytes, at least one: an empty text is inline with length 0
    // and has no entry. Location is the relative address of the entry, its one block up to
    // ValueBlocks.SingleBlockLimit bytes or the directory of its chunks above, as a value of kind Block or
    // Chunked lies; the blocks belong to the entry and are of kind SharedText. The block that holds the
    // descriptor holds one reference to the entry until it is freed (Text.TextTable).
    Text = 4,

    // The value is a List whose elements hold descriptors: elements that are a String, a Binary or a List,
    // or an owned value with one of them (Roots.StructureLayout). Its form is told by this kind, never by
    // its length. Length is the number of its elements, not of bytes. The elements are records of the
    // structure of the element declaration, StructureLayout.Size bytes each, in their order, and a record
    // lies whole in one block; nothing sorts or merges them. Location is 0 for an empty List. For a List of
    // at most StructureLayout.RecordsPerChunk elements it is the relative address of one block of kind Value
    // that holds its records; for a longer one, of a directory of kind ChunkDirectory with one descriptor of
    // kind Block for each chunk of kind Chunk, every chunk but the last holding RecordsPerChunk records. The
    // parent of that block or directory is the block that holds this descriptor. In a block of records, a
    // value of at most ValueBlocks.InlineLimit bytes that a descriptor of a record names lies inline after
    // the records, its Location an offset from the start of that block, its header included; a larger value
    // lies in blocks of its own, whose parent is that block.
    Records = 5,
}
