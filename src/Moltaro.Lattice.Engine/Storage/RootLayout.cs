using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

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
// The nullable fields have their bits in the order of their field identities, the first in the
// lowest bit of the first byte. A bit is set while its field is null; the bits after the last
// nullable field are zero. The bytes of a fixed field that is null are zero, and so is the
// descriptor of a null String or Binary.
//
// A String or Binary value is placed by its encoded length; DescriptorKindEnum says what its
// descriptor holds in each case:
// - at most InlineLimit bytes: inline, in the root block after the fixed part, when the root block
//   still fits one block. An empty value is inline with length 0;
// - any other value of at most SingleBlockLimit bytes: in one block of its own, of kind Value;
// - beyond that: chunked, in chunks of kind Chunk of ChunkPayload bytes, every chunk but the last
//   full, and one directory block of kind ChunkDirectory with one 16-byte descriptor for each
//   chunk. A value has at most the chunks one directory block holds, ValueBlocks.MostChunks, so it
//   takes at most 268,173,376 bytes; a larger one is ValidationFailed with the field named, refused
//   by the validation of the core and, as the guard of the stored form, by the Set… member of
//   RootWriter: a fixed rule of the format (decision O19 of Epic 1). A chunk
//   boundary is not a boundary of the value: a character may continue in the next chunk.
// The parent of a value block and of a directory is the root block; the parent of a chunk is its
// directory. A null String or Binary is null in the bitmap, and its descriptor is all zero.
//
// Text is Plain, every value stored on its own, in the database encoding that the schema declares
// (LatticeSchema.StringEncoding, Q04.1): in a UTF-8 database the UTF-8 of its characters, in a
// UTF-16 database its code units.
//
// The layout is compiled once into plain tables and never changes afterwards, so one layout can
// serve any number of writers and readers on any number of threads.
internal sealed class RootLayout
{
    // Every field by LatticeField.Ordinal, its position in the entity type: one array index finds
    // it, without a lookup by name or by identity.
    private readonly StoredField[] _fields;

    // One bit for every field that is not nullable, by LatticeField.Ordinal: what Complete requires.
    private readonly byte[] _requiredFields;

    // A layout is made by Compile only.
    private RootLayout(
        LatticeEntityType entityType,
        LatticeStringEncodingEnum stringEncoding,
        StoredField[] fields,
        byte[] requiredFields,
        int nullableCount,
        int descriptorOffset,
        int descriptorCount,
        int fixedSize)
    {
        EntityType = entityType;
        StringEncoding = stringEncoding;
        _fields = fields;
        _requiredFields = requiredFields;
        NullableCount = nullableCount;
        DescriptorOffset = descriptorOffset;
        DescriptorCount = descriptorCount;
        FixedSize = fixedSize;
        ImageCapacity = (int)Math.Min(fixedSize + (long)InlineLimit * descriptorCount, Arena.MaxBlockSize);
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
    public static RootLayout Compile(LatticeEntityType entityType, LatticeSchema schema)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(schema);

        // By reference: a schema compiled again from the same descriptor has entity types of its own.
        if (!schema.EntityTypes.Contains(entityType))
        {
            throw new ArgumentException($"The entity type {entityType.Name} is not an entity type of the given schema.", nameof(entityType));
        }

        var declared = entityType.Fields;

        foreach (var field in declared)
        {
            ThrowIfNotStorable(entityType, field);
        }

        var inIdentityOrder = declared.OrderBy(field => field.Id).ToArray();
        var fields = new StoredField[declared.Count];
        var requiredFields = new byte[BitBytes(declared.Count)];
        var nullBits = new int[declared.Count];
        var nullableCount = 0;

        // The bits of the null bitmap go to the nullable fields in identity order, whatever their type.
        foreach (var field in inIdentityOrder)
        {
            if (field.Declaration.IsNullable)
            {
                nullBits[field.Ordinal] = nullableCount++;
            }
            else
            {
                nullBits[field.Ordinal] = StoredField.NoNullBit;
                requiredFields[field.Ordinal / 8] |= (byte)(1 << (field.Ordinal % 8));
            }
        }

        // The fixed fields first and the descriptors after them, each group in identity order.
        var offset = BlockHeader.Size + BitBytes(nullableCount);

        foreach (var field in inIdentityOrder.Where(field => !IsVariable(field)))
        {
            var (type, width) = FixedTypeOf(field.Declaration);

            fields[field.Ordinal] = new StoredField(field, type, width, offset, nullBits[field.Ordinal]);
            offset += width;
        }

        var descriptorOffset = offset;
        var descriptorCount = 0;

        foreach (var field in inIdentityOrder.Where(IsVariable))
        {
            var type = field.Declaration.Type == LatticeFieldTypeEnum.String ? StoredTypeEnum.String : StoredTypeEnum.Binary;

            fields[field.Ordinal] = new StoredField(field, type, ValueBlocks.DescriptorSize, offset, nullBits[field.Ordinal]);
            offset += ValueBlocks.DescriptorSize;
            descriptorCount++;
        }

        if (offset > Arena.MaxBlockSize)
        {
            throw new LatticeException(
                LatticeErrorEnum.InvalidSchema,
                $"The fixed part of a root of the entity type {entityType.Name} takes {offset} bytes, more than the {Arena.MaxBlockSize} bytes of one block.");
        }

        return new RootLayout(
            entityType,
            schema.StringEncoding,
            fields,
            requiredFields,
            nullableCount,
            descriptorOffset,
            descriptorCount,
            offset);
    }

    // The entity type the layout was compiled for.
    public LatticeEntityType EntityType { get; }

    // The bytes of the fixed part of a root: the header, the nullable bitmap, the fixed fields and
    // the descriptors, as the comment of the class adds them up. At most Arena.MaxBlockSize.
    public int FixedSize { get; }

    // The version of this layout, which the header of every block of a root written with it carries
    // (BlockHeader.LayoutVersion). It is 1 for every layout of S05; how a changed layout is numbered
    // belongs to S37.
    public uint LayoutVersion => 1;

    // The most encoded bytes of a value stored inline, in the root block.
    public const int InlineLimit = 64;

    // The most encoded bytes of a value stored in one block of its own. A larger value is chunked.
    public const int SingleBlockLimit = 4 * 1024;

    // The payload bytes of one chunk: the largest payload of a block, Arena.MaxBlockSize less the
    // 32-byte header. Every chunk of a value but the last holds this many.
    public const int ChunkPayload = 65_504;

    // The text encoding of the database, in which every String of these roots is stored.
    internal LatticeStringEncodingEnum StringEncoding { get; }

    // How many fields are nullable, and so how many bits the null bitmap has.
    internal int NullableCount { get; }

    // Where the null bitmap begins: right after the header.
    internal const int NullBitmapOffset = BlockHeader.Size;

    // The bytes of the null bitmap.
    internal int NullBitmapLength => BitBytes(NullableCount);

    // Where the descriptors of the String and Binary fields begin, and how many there are: they
    // close the fixed part, 16 bytes each, in identity order.
    internal int DescriptorOffset { get; }

    internal int DescriptorCount { get; }

    // The most bytes a root block of this layout can take: the fixed part with every String and
    // Binary value inline, never more than one block.
    internal int ImageCapacity { get; }

    // How many fields the entity type has, and so how many bits a set of its fields takes.
    internal int FieldCount => _fields.Length;

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

        if ((uint)ordinal >= (uint)_fields.Length || !ReferenceEquals(_fields[ordinal].Field, field))
        {
            throw new ArgumentException($"The field {field.Name} is not a field of the entity type {EntityType.Name}.", nameof(field));
        }

        return ref _fields[ordinal];
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

    private static bool IsVariable(LatticeField field) =>
        field.Declaration.Type is LatticeFieldTypeEnum.String or LatticeFieldTypeEnum.Binary;

    // Owned values and collections come with S18 and S19, references with S18, Dictionary text with
    // S13.
    private static void ThrowIfNotStorable(LatticeEntityType entityType, LatticeField field)
    {
        var declaration = field.Declaration;

        if (declaration.Type is LatticeFieldTypeEnum.Owned or LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Map or LatticeFieldTypeEnum.Set or LatticeFieldTypeEnum.Reference)
        {
            throw new NotSupportedException($"The field {entityType.Name}.{field.Name} is {declaration.Type}, which a root cannot store yet.");
        }

        if (declaration.StringStorage == LatticeStringStorageEnum.Dictionary)
        {
            throw new NotSupportedException($"The field {entityType.Name}.{field.Name} is a String with Dictionary storage, which a root cannot store yet.");
        }
    }

    // The stored type and the width of a field that is neither a String nor a Binary.
    private static (StoredTypeEnum Type, int Width) FixedTypeOf(LatticeValueDeclaration declaration) => declaration.Type switch
    {
        LatticeFieldTypeEnum.Boolean => (StoredTypeEnum.Boolean, 1),
        LatticeFieldTypeEnum.Float32 => (StoredTypeEnum.Float32, 4),
        LatticeFieldTypeEnum.Float64 => (StoredTypeEnum.Float64, 8),
        LatticeFieldTypeEnum.Decimal => (StoredTypeEnum.Decimal, 16),
        LatticeFieldTypeEnum.Guid => (StoredTypeEnum.Guid, 16),
        LatticeFieldTypeEnum.DateTimeOffset => (StoredTypeEnum.DateTimeOffset, 8),
        LatticeFieldTypeEnum.DateOnly => (StoredTypeEnum.DateOnly, 4),
        LatticeFieldTypeEnum.TimeOnly => (StoredTypeEnum.TimeOnly, 8),
        LatticeFieldTypeEnum.TimeSpan => (StoredTypeEnum.TimeSpan, 8),
        LatticeFieldTypeEnum.Enum => IntegerTypeOf(declaration.EnumType!.UnderlyingType),
        _ => IntegerTypeOf(declaration.Type),
    };

    private static (StoredTypeEnum Type, int Width) IntegerTypeOf(LatticeFieldTypeEnum type) => type switch
    {
        LatticeFieldTypeEnum.Int8 => (StoredTypeEnum.SignedInteger, 1),
        LatticeFieldTypeEnum.UInt8 => (StoredTypeEnum.UnsignedInteger, 1),
        LatticeFieldTypeEnum.Int16 => (StoredTypeEnum.SignedInteger, 2),
        LatticeFieldTypeEnum.UInt16 => (StoredTypeEnum.UnsignedInteger, 2),
        LatticeFieldTypeEnum.Int32 => (StoredTypeEnum.SignedInteger, 4),
        LatticeFieldTypeEnum.UInt32 => (StoredTypeEnum.UnsignedInteger, 4),
        LatticeFieldTypeEnum.Int64 => (StoredTypeEnum.SignedInteger, 8),
        LatticeFieldTypeEnum.UInt64 => (StoredTypeEnum.UnsignedInteger, 8),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not an integer type."),
    };
}
