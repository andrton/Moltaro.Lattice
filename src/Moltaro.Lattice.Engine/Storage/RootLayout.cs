using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage;

// The compiled layout of the roots of one entity type: where each field of an entity stands in its
// root block, and where a variable value goes (milestone contract, "Native layout and allocation";
// Q16, "Record-oriented layout with separate variable payloads"). It is compiled from the schema
// before any data of the entity type is admitted. RootWriter, RootReader and RootStorage take from
// it where every value is, so that nothing is looked up by name while a root is written or read.
//
// The fixed part of a root comes first in the root block, in this order:
// - the 32-byte block header, BlockHeader;
// - the nullable bitmap, the validity bits of the nullable fields (Q03.2);
// - the fixed-width fields, in the order of their field identities. Each has the width its type has
//   in the scalar catalog (Q03.1, Q03.4.1): a small integer is not widened, and an Enum field has
//   the width of the underlying type of its enum type. A nullable field keeps its place when it is
//   null;
// - one ValueDescriptor of 16 bytes for each variable field, a String or a Binary, in the order of
//   their field identities.
// FixedSize counts these four parts. An offset inside a root block, that of a descriptor and that
// of an inline value, is counted from the same start, the header included.
//
// A String or Binary value is placed by its encoded length:
// - at most InlineLimit bytes: inline, in the tail of the root block after the fixed part, while
//   the root still fits its block;
// - otherwise at most SingleBlockLimit bytes: whole in one block of kind Value;
// - beyond that: in chunks of kind Chunk of at most ChunkPayload bytes each, listed in order by a
//   bounded directory of kind ChunkDirectory. A chunk boundary is not a boundary of the value: a
//   character may continue in the next chunk.
// DescriptorKindEnum says what the descriptor holds in each case.
//
// Text is Plain, every value stored on its own, in the database encoding that the schema declares
// (LatticeSchema.StringEncoding): UTF-8 or UTF-16 for the whole database (Q04.1). Dictionary text
// comes with S13; owned values, Lists and references with S18; Maps and Sets with S19.
internal sealed class RootLayout
{
    // A layout is made by Compile only.
    private RootLayout()
    {
    }

    // Compiles the layout of an entity type of the schema; the schema gives what the entity type
    // alone does not, such as its string encoding and its enum types. The fixed part has to fit one
    // block: an entity type whose FixedSize would exceed Arena.MaxBlockSize is refused with
    // LatticeException, InvalidSchema, here and so before any data is admitted, as the milestone
    // contract asks of schema compilation.
    public static RootLayout Compile(LatticeEntityType entityType, LatticeSchema schema) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The entity type the layout was compiled for.
    public LatticeEntityType EntityType =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The bytes of the fixed part of a root: the header, the nullable bitmap, the fixed fields and
    // the descriptors, as the comment of the class lists them. At most Arena.MaxBlockSize.
    public int FixedSize =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The version of this layout: the number by which a block header names the layout its payload
    // was written in (BlockHeader.LayoutVersion).
    public uint LayoutVersion =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The most encoded bytes of a value stored inline, in the tail of the root block.
    public const int InlineLimit = 64;

    // The most encoded bytes of a value stored whole in one block of kind Value. A larger value is
    // chunked.
    public const int SingleBlockLimit = 4 * 1024;

    // The most payload bytes of one chunk: the largest payload of a block, Arena.MaxBlockSize less
    // the 32-byte header.
    public const int ChunkPayload = 65_504;
}
