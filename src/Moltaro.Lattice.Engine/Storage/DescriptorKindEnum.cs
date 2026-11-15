namespace Moltaro.Lattice.Storage;

// Where a variable value of a root lies, as the Kind of its ValueDescriptor says, and with it what
// the Length and the Location of the descriptor mean (milestone contract, "Native layout and
// allocation"). RootLayout says which value takes which kind. Null is zero, so a descriptor whose
// bytes are all zero holds no value.
internal enum DescriptorKindEnum : uint
{
    // The value is null. Length and Location describe nothing.
    Null = 0,

    // The value lies in the tail of the root block, after its fixed part. Length is its encoded
    // bytes; Location is the offset of its first byte from the start of the root block, the block
    // header included, as RootLayout.FixedSize counts.
    Inline = 1,

    // The value lies whole in one block of kind Value. Length is its encoded bytes; Location is the
    // relative address of that block, as ArenaAddress.Value gives it.
    Block = 2,

    // The value lies in chunks, blocks of kind Chunk, in the order of its bytes. Length is its
    // encoded bytes; Location is the relative address of its chunk directory, the block of kind
    // ChunkDirectory that lists the chunks.
    Chunked = 3,

    // The value is text that a text entry of the database holds, not the root. Location is the
    // stable handle of that entry (milestone contract, "Collections and strings"). Text entries come
    // with S13; no value of S05 has this kind.
    Text = 4,
}
