using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage;

// The compiled layout of the roots of one entity type: where each field of an entity stands in its
// root block, and where a variable value goes (milestone contract, "Native layout and allocation";
// Q16, "Record-oriented layout with separate variable payloads"). RootWriter, RootReader and
// RootStorage take from it where every value is.
//
// The fixed part of a root comes first in the root block, packed without padding, in this order:
// - the 32-byte block header, BlockHeader;
// - the nullable bitmap: n / 8 bytes, rounded up, for the n nullable fields;
// - the fixed-width fields, in the order of their field identities, each at the width of its type
//   in the scalar catalog (Q03.1, Q03.4.1): a small integer is not widened, a Boolean is one byte,
//   0 or 1, and an Enum field has the width of the underlying type of its enum type. A nullable
//   field keeps its place when it is null;
// - one ValueDescriptor of 16 bytes for each String and Binary field, in the order of their field
//   identities.
// FixedSize is the sum of these four parts. An offset inside a root block, that of a descriptor and
// that of an inline value, is counted from the same start, the header included. A value of more
// than one byte is stored in the byte order of the machine, as the block header and the
// descriptors are: native memory is never a durable or a wire form, and the byte order of those
// forms belongs to their sprints.
//
// A String or Binary value is placed by its encoded length; DescriptorKindEnum says what its
// descriptor holds in each case:
// - at most InlineLimit bytes: inline, in the root block after the fixed part, when the root block
//   still fits one block. An empty value is inline with length 0;
// - any other value of at most SingleBlockLimit bytes: in one block of its own, of kind Value;
// - beyond that: chunked, in chunks of kind Chunk of ChunkPayload bytes, every chunk but the last
//   full, and one directory block of kind ChunkDirectory with one 16-byte descriptor for each
//   chunk. A value that needs more chunks than one directory block holds is WriteRejected,
//   reported by RootWriter.Complete. A chunk boundary is not a boundary of the value: a character
//   may continue in the next chunk.
// The parent of a value block and of a directory is the root block; the parent of a chunk is its
// directory. A null String or Binary is null in the bitmap, and its descriptor is all zero.
//
// Text is Plain, every value stored on its own, in the database encoding that the schema declares
// (LatticeSchema.StringEncoding, Q04.1): in a UTF-8 database the UTF-8 of its characters, in a
// UTF-16 database its code units.
internal sealed class RootLayout
{
    // A layout is made by Compile only.
    private RootLayout()
    {
    }

    // Compiles the layout of an entity type of the schema; the schema gives what the entity type
    // alone does not, such as its string encoding and its enum types. Refuses:
    // - an entity type that is not of the schema, with ArgumentException;
    // - an entity type with a field S05 cannot store yet, an Owned, List, Map, Set or Reference
    //   field or a String with Dictionary storage, with NotSupportedException that names the field.
    //   S13, S18 and S19 extend the layout;
    // - an entity type whose FixedSize would exceed Arena.MaxBlockSize, with LatticeException,
    //   InvalidSchema, that names the entity type: the fixed part has to fit one block, and the
    //   milestone contract asks that an oversized fixed layout be reported before data is admitted.
    public static RootLayout Compile(LatticeEntityType entityType, LatticeSchema schema) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The entity type the layout was compiled for.
    public LatticeEntityType EntityType =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The bytes of the fixed part of a root: the header, the nullable bitmap, the fixed fields and
    // the descriptors, as the comment of the class adds them up. At most Arena.MaxBlockSize.
    public int FixedSize =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The version of this layout, which the header of every block of a root written with it carries
    // (BlockHeader.LayoutVersion). It is 1 for every layout of S05; how a changed layout is numbered
    // belongs to S37.
    public uint LayoutVersion =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The most encoded bytes of a value stored inline, in the root block.
    public const int InlineLimit = 64;

    // The most encoded bytes of a value stored in one block of its own. A larger value is chunked.
    public const int SingleBlockLimit = 4 * 1024;

    // The payload bytes of one chunk: the largest payload of a block, Arena.MaxBlockSize less the
    // 32-byte header. Every chunk of a value but the last holds this many.
    public const int ChunkPayload = 65_504;
}
