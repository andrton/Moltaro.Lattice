namespace Moltaro.Lattice.Storage.Roots;

// The compiled layout of one structure: the fields of an entity type in its root block, after the block
// header, or the record of one element of a List. One stored form serves both: a null bitmap, the
// fixed-width fields and the 16-byte descriptors that close it, packed without padding, Size bytes in all
// (docs/design/milestone-1-engine-contract.md, "Native layout and allocation" and "Collections and
// strings"). RootLayout lays the structure of a root after the 32 bytes of the header, and lays the records
// of the elements of a List one after the other at the stride Size.
//
// The fields. An owned value is flattened into the structure that holds it: it takes no block and no hop,
// and its fields stand among the fields of the structure, its fixed-width fields among the fixed-width
// fields and its descriptors among the descriptors, each group in the order of the paths of the field
// identities, the fields of an owned value at the place of the owned value. So the descriptors of a
// structure are one run, whatever owned values it holds. A field of an owned type that stands at two
// positions has a place at each. A Reference is a field of the type of its target's key: an integer at its
// width, a Guid of 16 bytes, or the descriptor of a String; it is null by its null bit and never looked up.
// A List is one descriptor (StoredTypeEnum.List).
//
// The bits. Each bit of the bitmap is set while its value is null, in the order of the paths, the first in
// the lowest bit of the first byte, and the bits after the last are zero. A nullable field has one. A
// nullable owned value has one, its presence bit, set while it is absent, and every field beneath it has a
// bit of its own whether it is nullable or not, set while it or an owned value above it is absent, as
// LatticeIndexPart.CanBeNull says. An absent owned value keeps its width: every bit beneath it is set and
// every byte of its fields is zero, its descriptors included. The record of an element that is nullable
// has one bit for the element, as a nullable field has.
//
// A List of these records. A record that holds no descriptor (fixed-width values, references by an integer
// or a Guid, owned values of such fields, each with its bitmap in the record) is packed: the List lies as
// the bytes of a Binary value do, Size bytes for each element, inline, in one block or in chunks, and a
// record may continue in the next chunk. A record that holds descriptors lies whole in one block, its List
// of kind DescriptorKindEnum.Records: RecordsPerChunk records a chunk, each with room for a value of
// ValueBlocks.InlineLimit bytes inline for each of its descriptors. The bound of a List, MostElements, is
// what one directory of chunks holds.
//
// Two rules of the schema language follow, the same in every process: the fixed part of a root, its owned
// values included, fits one block; and the record of an element of a List fits one chunk, a record that
// holds descriptors with the room of a value inline for each. A schema that breaks one fails the open with
// InvalidSchema, as a fixed part beyond one block does.
//
// Compiled once with its RootLayout and never changed afterwards, so one layout serves any number of
// writers and readers on any number of threads.
internal sealed class StructureLayout
{
    // A layout is compiled by its RootLayout only.
    private StructureLayout(int size, int nullableCount, int descriptorCount, int recordsPerChunk, int mostElements)
    {
        Size = size;
        NullableCount = nullableCount;
        DescriptorCount = descriptorCount;
        RecordsPerChunk = recordsPerChunk;
        MostElements = mostElements;
    }

    // The bytes of the structure: the null bitmap, the fixed-width fields and the descriptors. For the record
    // of an element, the stride of the records of its List.
    public int Size { get; }

    // How many bits the null bitmap has: one for each nullable field, each presence bit and each field
    // beneath a nullable owned value, and one for a nullable element.
    public int NullableCount { get; }

    // The bytes of the null bitmap.
    public int NullBitmapLength => RootLayout.BitBytes(NullableCount);

    // How many descriptors close the structure, those of its owned values included.
    public int DescriptorCount { get; }

    // Whether the structure holds a descriptor, and so whether a List of such records is of kind Records.
    public bool HoldsDescriptors => DescriptorCount > 0;

    // For the record of an element that holds descriptors: how many records one chunk holds, each with the
    // room of a value inline for each of its descriptors, ValueBlocks.ChunkPayload divided by Size plus
    // ValueBlocks.InlineLimit times DescriptorCount, rounded down; at least 1 by the rule of the schema
    // language. 0 for a record that holds no descriptor, whose List lies as bytes.
    public int RecordsPerChunk { get; }

    // For the record of an element: the most elements a List of such records holds, the bound of a List, a
    // fixed rule of the storage format as the bound of one String or Binary value is. For a record that holds
    // no descriptor, ValueBlocks.MostValueBytes divided by Size, rounded down; for one that holds descriptors,
    // RecordsPerChunk times ValueBlocks.MostChunks.
    public int MostElements { get; }
}
