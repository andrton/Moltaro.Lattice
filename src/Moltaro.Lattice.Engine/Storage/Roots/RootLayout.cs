using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// The compiled layout of the roots of one entity type: where each value of an entity stands in its root
// block, and where a variable value goes (milestone contract, "Native layout and allocation"; Q16,
// "Record-oriented layout with separate variable payloads"). RootWriter, RootReader and RootStorage take from
// it where every value is. The fixed part of a root is its 32-byte header, BlockHeader, and after it the
// structure of the fields of the entity type (StructureLayout): the null bitmap, the fixed-width fields and
// the descriptors, packed without padding, the fields of its owned values flattened into it. FixedSize is the
// sum of the header and the structure. An offset inside a root block, that of a field, of a descriptor and of
// an inline value, is counted from the start of the block, the header included. A value of more than one byte
// is stored in the byte order of the machine, as the block header and the descriptors are: native memory is
// never a durable or a wire form, and the byte order of those forms belongs to their sprints.
//
// A fixed-width field takes the width of its type in the scalar catalog (Q03.1, Q03.4.1): a small integer is
// not widened, a Boolean is one byte, 0 or 1, an Enum field has the width of the underlying type of its enum
// type, and a Reference that of the key of its target. A nullable field keeps its place when it is null. The
// bytes of a fixed field that is null are zero, and so is the descriptor of a null String, Binary or List.
//
// A String or Binary value, and a List whose elements hold no descriptor, which lies as the bytes of a Binary
// value, is placed by its encoded length, by the bounds of a value that Values.ValueBlocks sets, InlineLimit,
// SingleBlockLimit and ChunkPayload; DescriptorKindEnum says what its descriptor holds in each case:
// - at most InlineLimit bytes: inline, in the root block after the fixed part, when the root block
//   still fits one block. An empty value is inline with length 0;
// - any other value of at most SingleBlockLimit bytes: in one block of its own, of kind Value;
// - beyond that: chunked, in chunks of kind Chunk of ChunkPayload bytes, every chunk but the last
//   full, and one directory block of kind ChunkDirectory with one 16-byte descriptor for each
//   chunk. A value has at most the chunks one directory block holds, ValueBlocks.MostChunks, so it
//   takes at most 268,173,376 bytes; a larger one is ValidationFailed with the field named, refused
//   by the validation of the core and, as the guard of the stored form, by RootWriter: a fixed rule
//   of the format (decision O19 of Epic 1). A chunk boundary is not a boundary of the value: a character,
//   or the record of an element of a List, may continue in the next chunk.
// A List whose elements hold descriptors lies as records, never inline (DescriptorKindEnum.Records).
// The parent of a value block, of a directory and of a block of records is the block that holds its
// descriptor, the root block for a value of the root; the parent of a chunk is its directory. A null String,
// Binary or List is null in the bitmap, and its descriptor is all zero.
//
// Text is stored in the database encoding that the schema declares (LatticeSchema.StringEncoding,
// Q04.1): in a UTF-8 database the UTF-8 of its characters, in a UTF-16 database its code units. A field
// with Plain storage stores every value on its own, placed as above. A field with Dictionary storage
// has the same descriptor, and a text that is not empty lies in an entry of the text table of the
// database, shared by every block that holds the same exact bytes, which the descriptor names by the
// kind Text, the length of the text and the relative address of the entry (Text.TextTable): never inline,
// so it takes nothing of the root block but its descriptor, and the entry is placed as a value out of line
// is, one block up to SingleBlockLimit bytes and chunks above. The empty text is inline with length 0, as in
// a Plain field. So the layout of an entity type is the same whichever storage its String fields select.
// A String field that is part of a secondary index stores a text longer than InlineLimit in an entry of
// the text table as well, whatever its storage, so that the entry of the index can share it with its
// root; such an entry is found by its bytes like any other, so equal long texts of an indexed Plain field
// are stored once, and a text of InlineLimit bytes or less is placed as a Plain value is
// (StoredField.LiesInTextEntry; decision on index structures, the note of 19 November 2026). Adding or
// dropping an index therefore changes where the long texts of its fields lie.
//
// The layout is compiled once into plain tables and never changes afterwards, so one layout can
// serve any number of writers and readers on any number of threads.
internal sealed class RootLayout
{
    // Every field by LatticeField.Ordinal, its position in the entity type: one array index finds it, without
    // a lookup by name or by identity. It is the table of the places of the structure, whose first positions
    // are the fields of the entity type (StructureLayout), held here as well so that a field is found by one
    // load and one index, as before owned values were stored (JIT listing at full optimization).
    private readonly StoredField[] _fields;

    // One bit for every field that is not nullable, by LatticeField.Ordinal: what Complete requires.
    private readonly byte[] _requiredFields;

    // A layout is made by Compile only.
    private RootLayout(LatticeEntityType entityType, LatticeStringEncodingEnum stringEncoding, StructureLayout structure, byte[] requiredFields)
    {
        EntityType = entityType;
        StringEncoding = stringEncoding;
        Structure = structure;
        _fields = structure.SharedPlaces;
        _requiredFields = requiredFields;
        FieldCount = entityType.Fields.Count;
        NullableCount = structure.NullableCount;
        DescriptorOffset = structure.DescriptorOffset;
        DescriptorCount = structure.DescriptorCount;
        FixedSize = BlockHeader.Size + structure.Size;
        ImageCapacity = (int)Math.Min(FixedSize + (long)ValueBlocks.InlineLimit * DescriptorCount, Arena.MaxBlockSize);
    }

    // Compiles the layout of an entity type of the schema; the schema gives what the entity type
    // alone does not, such as its string encoding and its enum types. Refuses:
    // - an entity type that is not of the schema, with ArgumentException;
    // - an entity type the engine cannot store yet, with NotSupportedException that names what it holds: a
    //   Map or a Set at any depth, or an index over a field inside an owned value;
    // - an entity type whose FixedSize would exceed Arena.MaxBlockSize, its owned values included, or that
    //   holds a List whose record of an element does not fit one chunk, with LatticeException,
    //   InvalidSchema, that names the entity type: two rules of the schema language, which the milestone
    //   contract asks to be reported before data is admitted (StructureLayout).
    public static RootLayout Compile(LatticeEntityType entityType, LatticeSchema schema)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(schema);

        // By reference: a schema compiled again from the same descriptor has entity types of its own.
        if (!schema.EntityTypes.Contains(entityType))
        {
            throw new ArgumentException($"The entity type {entityType.Name} is not an entity type of the given schema.", nameof(entityType));
        }

        foreach (var index in entityType.Indexes)
        {
            if (index.Parts.Any(part => part.Path.Count > 1))
            {
                throw new NotSupportedException($"The index {entityType.Name}.{index.Name} is over a field inside an owned value, which a root cannot index yet.");
            }
        }

        // The fields that are part of an index: a part of an entity type that is stored is one of its fields.
        var indexed = entityType.Indexes.SelectMany(index => index.Parts).Select(part => part.Field).ToHashSet();
        var requiredFields = new byte[BitBytes(entityType.Fields.Count)];

        foreach (var field in entityType.Fields)
        {
            if (!field.Declaration.IsNullable)
            {
                requiredFields[field.Ordinal / 8] |= (byte)(1 << (field.Ordinal % 8));
            }
        }

        var structure = StructureLayout.OfRoot(entityType, indexed);

        if (BlockHeader.Size + structure.Size > Arena.MaxBlockSize)
        {
            throw new LatticeException(
                LatticeErrorEnum.InvalidSchema,
                $"The fixed part of a root of the entity type {entityType.Name} takes {BlockHeader.Size + structure.Size} bytes, more than the {Arena.MaxBlockSize} bytes of one block.");
        }

        return new RootLayout(entityType, schema.StringEncoding, structure, requiredFields);
    }

    // The entity type the layout was compiled for.
    public LatticeEntityType EntityType { get; }

    // The bytes of the fixed part of a root: the header, the nullable bitmap, the fixed fields and
    // the descriptors, as the comment of the class adds them up. At most Arena.MaxBlockSize.
    public int FixedSize { get; }

    // The layout version: the form of the bytes of a root as every node compiles it from the schema, one
    // version for the node and so for every layout. The header of every block of a root carries it
    // (BlockHeader.LayoutVersion), as does the envelope of every root sent to a child, and both are checked
    // on receipt; the handshake carries it in the low 16 bits of the layout format
    // (Replication.Protocol.LayoutFormat), so that two builds that store roots in other forms refuse each
    // other. It is raised when the same schema would compile to bytes an earlier build would read
    // otherwise: 2 since a field that is part of an index stores a value longer than 64 bytes as a text
    // entry.
    public const uint Version = 2;

    // The version of this layout, Version, as every layout of the node has it.
    public uint LayoutVersion => Version;

    // The text encoding of the database, in which every String of these roots is stored.
    internal LatticeStringEncodingEnum StringEncoding { get; }

    // How many bits the null bitmap has: the nullable fields, and the presence bits and the fields beneath a
    // nullable owned value (StructureLayout).
    internal int NullableCount { get; }

    // Where the null bitmap begins: right after the header.
    internal const int NullBitmapOffset = BlockHeader.Size;

    // The bytes of the null bitmap.
    internal int NullBitmapLength => BitBytes(NullableCount);

    // Where the descriptors begin, and how many there are: those of the String, Binary and List fields and of
    // the fields of the owned values, which close the fixed part, 16 bytes each, in the order of their paths.
    internal int DescriptorOffset { get; }

    internal int DescriptorCount { get; }

    // The place whose descriptor is the one at the index, 0 to DescriptorCount less one.
    internal ref readonly StoredField DescriptorField(int index) => ref Structure.DescriptorField(index);

    // The most bytes a root block of this layout can take: the fixed part with every String and
    // Binary value inline, never more than one block.
    internal int ImageCapacity { get; }

    // How many fields the entity type has, and so how many bits a set of its fields takes.
    internal int FieldCount { get; }

    // One bit for every field that is not nullable, by LatticeField.Ordinal.
    internal ReadOnlySpan<byte> RequiredFields => _requiredFields;

    // The bytes of a set of one bit for each field.
    internal static int BitBytes(int bits) => (bits + 7) / 8;

    // Where the field stands in the root block and how it is stored. A field that is not a field of
    // the entity type of the layout is refused with ArgumentException: a field of another entity
    // type that has the same position is a different object, so the comparison is by reference.
    internal ref readonly StoredField StoredFieldOf(LatticeField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        var ordinal = field.Ordinal;

        if ((uint)ordinal >= (uint)FieldCount || !ReferenceEquals(_fields[ordinal].Field, field))
        {
            throw new ArgumentException($"The field {field.Name} is not a field of the entity type {EntityType.Name}.", nameof(field));
        }

        return ref _fields[ordinal];
    }

    // The structure of the root after its header: its null bitmap, its fixed-width fields and its
    // descriptors, the fields of its owned values flattened into it (StructureLayout).
    internal StructureLayout Structure { get; }

    // Where the field at the end of the path stands in the root block and how it is stored. The path is that
    // of LatticeIndexPart.Path: from a field of the entity type through owned values down to the field, one
    // element for a field of the entity type itself, which StoredFieldOf(LatticeField) gives. An owned value
    // on the path is flattened into the root (StructureLayout), so a field inside it has its offset in the
    // root block and, beneath a nullable owned value, a null bit of its own; the path of an owned value
    // itself gives its presence bit (StoredTypeEnum.Owned). Resolved once, by the layout of an index; an empty
    // path, a first step that is not a field of the entity type and a step that is not a field of the owned
    // type of the step before it are refused with ArgumentException.
    internal ref readonly StoredField StoredFieldOf(IReadOnlyList<LatticeField> path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Count == 0)
        {
            throw new ArgumentException($"A path into {EntityType.Name} has at least one field.", nameof(path));
        }

        var position = Structure.PositionOf(path);

        if (position < 0)
        {
            throw new ArgumentException($"The path {string.Join(".", path.Select(step => step.Name))} is not a path of the entity type {EntityType.Name}.", nameof(path));
        }

        return ref Structure.Place(position);
    }

    // The payload of a root block written with this layout, after checking that the block is one:
    // a valid block of the arena, of kind Root, with the layout version of this layout. Each refusal
    // is ArgumentException. RootReader and RootStorage.Free recognize a root by the same checks.
    internal Span<byte> RootPayload(Arena arena, ArenaAddress root)
    {
        ArgumentNullException.ThrowIfNull(arena);

        ref var header = ref arena.Header(root);

        if (header.Kind != BlockKindEnum.Root)
        {
            throw new ArgumentException($"The block at {root} is of kind {header.Kind}, not a root.", nameof(root));
        }

        if (header.LayoutVersion != LayoutVersion)
        {
            throw new ArgumentException($"The root at {root} has the layout version {header.LayoutVersion}, not {LayoutVersion} of the layout of {EntityType.Name}.", nameof(root));
        }

        return arena.Payload(root);
    }
}
