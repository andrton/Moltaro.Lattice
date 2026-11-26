namespace Moltaro.Lattice.Storage;

// What a block holds, as the Kind field of its header says. No kind has the value zero.
internal enum BlockKindEnum : ushort
{
    // The fixed part of one entity, which its root entry points to. A root has no parent.
    Root = 1,

    // One owned variable value of an entity, held whole in one block outside its root.
    Value = 2,

    // One bounded piece of a large value, or of the records of a List whose elements hold
    // descriptors. A chunk boundary is not a boundary of the value: a character, or the record of an
    // element of a List whose elements hold no descriptor, may continue in the next chunk; a record that
    // holds descriptors lies whole in one block (DescriptorKindEnum.Records).
    Chunk = 3,

    // The bounded directory of the chunks of one large value.
    ChunkDirectory = 4,

    // Text of the database-wide dictionary. It belongs to a text entry instead of a root.
    SharedText = 5,

    // A node of the ordered tree of a secondary index (Ordered.IndexNode). It belongs to the index:
    // the root handle of its header is default, and it has no parent.
    IndexNode = 6,

    // A collection's entries descriptor followed by its lookup slots or the addresses of their chunks.
    Lookup = 7,

    // The inactive link of a reusable lookup slot after its last reader leaves.
    ColdLookupSlots = 8,

    // The directory of a shared text. Its stable ID owns it.
    SharedTextDirectory = 9,

    // A chunk of a shared text. The ID and descriptor offset find the edge
    // in the current directory without a physical parent backlink.
    SharedTextChunk = 10,
}
