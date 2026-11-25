using System.Collections.Immutable;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// The compiled layout of the roots of one entity type. RootWriter, RootReader and RootStorage use
// it to find a root's fields and descriptors. The fixed part includes the block header and the
// entity's StructureLayout. Root field offsets and inline locations both count from the start of
// the block, including its header. A payload span begins immediately after the header.
//
// The fixed-width fields use the widths of their scalar types. A nullable field keeps its place.
// The writer zeroes the fixed-width bytes under a null bit. Readers skip those bytes even if a
// received root has other contents there. A null descriptor must be zero.
//
// ValueBlocks gives the placement bounds for variable-length values. StoredField.LiesInTextEntry
// selects shared storage for nonempty Dictionary text and long text used in a secondary index.
// Adding or dropping an index can therefore change where long texts of its fields are placed.
//
// Compiled once into plain immutable tables for all readers and writers. The stored layout, offset
// frames and transfer format are described in docs/design/arena-protocol.md, "Stored structures
// and root transfer."
internal sealed class RootLayout
{
    // Every field by LatticeField.Ordinal, its position in the entity type: one array index finds it, without
    // a lookup by name or by identity. It is the table of the places of the structure, whose first positions
    // are the fields of the entity type (StructureLayout), held here as well so that a field is found by one
    // load and one index rather than through the structure, a load more for every value a root writes or
    // reads (JIT listing at full optimization).
    private readonly ImmutableArray<StoredField> _fields;

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
    // - an entity type whose FixedSize would exceed Arena.MaxBlockSize, its owned values included, or that
    //   holds a collection whose entry does not fit one chunk, with LatticeException,
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

        // The places that are part of an index, by their paths: a field of the entity type, or a field inside
        // an owned value of it.
        var indexed = entityType.Indexes.SelectMany(index => index.Parts).Select(part => part.Path).ToList();
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

    // A native backlink records a block offset, which must lie exactly on a
    // descriptor of this layout rather than anywhere inside its fixed part.
    internal bool TryGetDescriptorIndex(uint offset, out int index)
    {
        var withinDescriptors = (long)offset - DescriptorOffset;
        if (withinDescriptors < 0 || withinDescriptors >= DescriptorCount * ValueBlocks.DescriptorSize ||
            withinDescriptors % ValueBlocks.DescriptorSize != 0)
        {
            index = -1;
            return false;
        }

        index = (int)withinDescriptors / ValueBlocks.DescriptorSize;
        return true;
    }

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

        if ((uint)ordinal >= (uint)FieldCount || !ReferenceEquals(_fields.ItemRef(ordinal).Field, field))
        {
            throw new ArgumentException($"The field {field.Name} is not a field of the entity type {EntityType.Name}.", nameof(field));
        }

        return ref _fields.ItemRef(ordinal);
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
