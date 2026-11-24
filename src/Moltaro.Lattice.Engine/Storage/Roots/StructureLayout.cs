using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

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
// record may continue in the next chunk (Packed). A record that holds descriptors lies whole in one block,
// its List of kind DescriptorKindEnum.Records: RecordsPerChunk records a chunk, each with room for a value of
// ValueBlocks.InlineLimit bytes inline for each of its descriptors. The bound of a List, MostElements, is
// what one directory of chunks holds.
//
// Two rules of the schema language follow, the same in every process: the fixed part of a root, its owned
// values included, fits one block (RootLayout); and the record of an element of a List fits one chunk, a
// record that holds descriptors with the room of a value inline for each. A schema that breaks one fails
// the open with InvalidSchema and the entity type named, as a fixed part beyond one block does.
//
// The places. Every value the structure holds has a place, a position in one plain table: the members of the
// structure first, the fields of an entity type by LatticeField.Ordinal or the one element of a record, and
// for every owned value among them the fields of its owned type in a run of their own, by their ordinals
// (ChildOf), so that a path is followed by one index a step. A List place has the layout of its element
// (ElementOf). The descriptors are also listed in their order, with the layouts of their elements, for the
// walk over what a structure owns (StructureWalk).
//
// Compiled once with its RootLayout and never changed afterwards, so one layout serves any number of
// writers and readers on any number of threads.
internal sealed class StructureLayout
{
    // The place at each position, and the declaration of what it holds: of its field, or of the element for
    // the one place of the record of an element.
    private readonly StoredField[] _places;

    private readonly LatticeValueDeclaration[] _declarations;

    // For an owned place, the position of the first field of its owned type; -1 for any other place.
    private readonly int[] _children;

    // For a List place, the layout of the record of its element; null for any other place.
    private readonly StructureLayout?[] _elements;

    // The positions of the places that are descriptors, in the order of their descriptors.
    private readonly int[] _descriptors;

    // Each place beneath a nullable owned value, with the null bit of the owned value right above it.
    private readonly (int Position, int Above)[] _beneath;

    private StructureLayout(
        int start,
        StoredField[] places,
        LatticeValueDeclaration[] declarations,
        int[] children,
        StructureLayout?[] elements,
        int[] descriptors,
        (int Position, int Above)[] beneath,
        int nullableCount,
        int descriptorOffset,
        int size,
        LatticeValueDeclaration? list,
        bool holdsNestedValues,
        bool holdsLists)
    {
        Start = start;
        _places = places;
        _declarations = declarations;
        _children = children;
        _elements = elements;
        _descriptors = descriptors;
        _beneath = beneath;
        NullableCount = nullableCount;
        DescriptorOffset = descriptorOffset;
        Size = size;
        List = list;
        HoldsNestedValues = holdsNestedValues;
        HoldsLists = holdsLists;

        if (list is not null && DescriptorCount > 0)
        {
            RecordsPerChunk = ValueBlocks.ChunkPayload / (size + (ValueBlocks.InlineLimit * DescriptorCount));
            MostElements = RecordsPerChunk * ValueBlocks.MostChunks;
        }
        else if (list is not null)
        {
            MostElements = ValueBlocks.MostValueBytes / size;
            Packed = PackedFor(list.Element!);
        }
    }

    // Where the structure begins, as the offsets of its places count: 32, the header of the block, for a
    // root, whose offsets count from the start of its block; 0 for the record of an element, whose offsets
    // count from the start of the record.
    public int Start { get; }

    // The bytes of the structure: the null bitmap, the fixed-width fields and the descriptors. For the record
    // of an element, the stride of the records of its List.
    public int Size { get; }

    // How many bits the null bitmap has: one for each nullable field, each presence bit and each field
    // beneath a nullable owned value, and one for a nullable element.
    public int NullableCount { get; }

    // The bytes of the null bitmap, which begins at Start.
    public int NullBitmapLength => RootLayout.BitBytes(NullableCount);

    // Where the descriptors begin, counted as the offsets of the places are.
    public int DescriptorOffset { get; }

    // How many descriptors close the structure, those of its owned values included.
    public int DescriptorCount => _descriptors.Length;

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

    // For the record of an element: the declaration of the List whose element it is, which makes the List a
    // record holds (LatticeValueDeclaration.Sequence); null for a root.
    public LatticeValueDeclaration? List { get; }

    // For the record of an element that holds no descriptor: how a List of it becomes bytes and back.
    public PackedElements? Packed { get; }

    // Whether the structure holds an owned value, a List or a Reference, at any depth.
    public bool HoldsNestedValues { get; }

    // Whether it holds a List, in its owned values included, whose bound a write is checked against before
    // it is submitted (Engine.Entities.RootValues.CheckListBounds); for the record of an element, whether
    // the element holds one.
    public bool HoldsLists { get; }

    // The place at the position.
    public ref readonly StoredField Place(int position) => ref _places[position];

    // The table of the places itself, for the layout of a root, which finds a field of its entity type in it
    // by one index (RootLayout).
    public ImmutableArray<StoredField> SharedPlaces => ImmutableCollectionsMarshal.AsImmutableArray(_places);

    // The declaration of what the place at the position holds.
    public LatticeValueDeclaration DeclarationOf(int position) => _declarations[position];

    // The position of a field of the owned type of the owned place at the position.
    public int ChildOf(int position, LatticeField field) => _children[position] + field.Ordinal;

    // The layout of the record of the element of the List place at the position.
    public StructureLayout ElementOf(int position) => _elements[position]!;

    // The place of the descriptor at the index, 0 to DescriptorCount less one, in the order of the descriptors.
    public ref readonly StoredField DescriptorField(int index) => ref _places[_descriptors[index]];

    // The layout of the element of the descriptor at the index when it is a List; null otherwise.
    public StructureLayout? DescriptorElement(int index) => _elements[_descriptors[index]];

    // Every place beneath a nullable owned value, by its position, with the null bit of the owned value right
    // above it, which has one since it lies beneath a nullable owned value too or is one: what the bits of a
    // structure agree on (see the class). The bit of such a place is set while that owned value is absent, and
    // when it is present the bit of a place that is not nullable is clear. Empty for a structure that holds no
    // nullable owned value, a root of scalar fields among them.
    public ReadOnlySpan<(int Position, int Above)> Beneath => _beneath;

    // The layout of the structure of the fields of an entity type, after the header of its root block. The
    // places the paths indexed lead to, as LatticeIndexPart.Path gives them, are parts of a secondary index:
    // a field of an owned type that stands at two positions is indexed at the one its path names. Refuses,
    // with LatticeException, InvalidSchema and the entity type named, the record of an element of a List that
    // does not fit one chunk; with NotSupportedException and the field named, a Map or a Set at any depth.
    public static StructureLayout OfRoot(LatticeEntityType entityType, IReadOnlyList<IReadOnlyList<LatticeField>> indexed) =>
        Compile(entityType, BlockHeader.Size, [.. entityType.Fields.Select(field => (field, field.Declaration))], indexed, list: null);

    // The position of the place a path leads to, a path as LatticeIndexPart.Path gives one, from a member of
    // a root down through owned values; -1 when a step is not a field of the owned type of the step before it.
    // A field of an owned type is one object at every position of the type, so the place of a step is the
    // child of the place before it whose field is that very object.
    public int PositionOf(IReadOnlyList<LatticeField> path)
    {
        var position = path[0].Ordinal;

        if ((uint)position >= (uint)_places.Length || !ReferenceEquals(_places[position].Field, path[0]))
        {
            return -1;
        }

        for (var step = 1; step < path.Count; step++)
        {
            if (_children[position] < 0)
            {
                return -1;
            }

            position = ChildOf(position, path[step]);

            if ((uint)position >= (uint)_places.Length || !ReferenceEquals(_places[position].Field, path[step]))
            {
                return -1;
            }
        }

        return position;
    }

    // The layout of the element of a List, whose record has the element as its one member, the List field
    // its place names.
    private static StructureLayout OfElement(LatticeEntityType entityType, LatticeField list, LatticeValueDeclaration declaration)
    {
        // A path never passes through a List (LatticeIndexPart), so nothing in a record is indexed.
        var element = Compile(entityType, 0, [(list, declaration.Element!)], [], declaration);
        var room = element.Size + (element.HoldsDescriptors ? ValueBlocks.InlineLimit * element.DescriptorCount : 0);

        if (room > ValueBlocks.ChunkPayload)
        {
            throw new LatticeException(
                LatticeErrorEnum.InvalidSchema,
                $"The record of an element of the List {entityType.Name}.{list.Name} takes {room} bytes with the room of its values inline, more than the {ValueBlocks.ChunkPayload} bytes of one chunk.");
        }

        return element;
    }

    private static StructureLayout Compile(
        LatticeEntityType entityType,
        int start,
        (LatticeField Field, LatticeValueDeclaration Declaration)[] members,
        IReadOnlyList<IReadOnlyList<LatticeField>> indexed,
        LatticeValueDeclaration? list)
    {
        var fields = new List<(LatticeField Field, LatticeValueDeclaration Declaration)>();
        var children = new List<int>();
        var parents = new List<int>();
        var paths = new List<LatticeField[]>();

        // The positions: the members in a run, then the fields of every owned value among them in a run of
        // their own, recursively, each with the owned place it lies in, -1 for a member, and the path that
        // leads to it.
        int Allocate(IReadOnlyList<(LatticeField Field, LatticeValueDeclaration Declaration)> run, int parent, LatticeField[] above)
        {
            var first = fields.Count;

            foreach (var member in run)
            {
                fields.Add(member);
                children.Add(-1);
                parents.Add(parent);
                paths.Add([.. above, member.Field]);
            }

            for (var index = 0; index < run.Count; index++)
            {
                if (run[index].Declaration.Type == LatticeFieldTypeEnum.Owned)
                {
                    children[first + index] = Allocate([.. run[index].Declaration.OwnedType!.Fields.Select(field => (field, field.Declaration))], first + index, paths[first + index]);
                }
            }

            return first;
        }

        Allocate(members, -1, []);

        var indexedPositions = Enumerable.Range(0, fields.Count).Where(position => indexed.Any(path => path.SequenceEqual(paths[position]))).ToHashSet();

        // The positions in the order of the paths of the field identities, each with whether a nullable owned
        // value lies above it.
        var inPathOrder = new List<(int Position, bool BeneathNullable)>();

        void Order(int first, int count, bool beneathNullable)
        {
            foreach (var position in Enumerable.Range(first, count).OrderBy(position => fields[position].Field.Id))
            {
                inPathOrder.Add((position, beneathNullable));

                var declaration = fields[position].Declaration;

                if (declaration.Type == LatticeFieldTypeEnum.Owned)
                {
                    Order(children[position], declaration.OwnedType!.Fields.Count, beneathNullable || declaration.IsNullable);
                }
            }
        }

        Order(0, members.Length, beneathNullable: false);

        var nullBits = new int[fields.Count];
        var nullableCount = 0;

        foreach (var (position, beneathNullable) in inPathOrder)
        {
            nullBits[position] = fields[position].Declaration.IsNullable || beneathNullable ? nullableCount++ : StoredField.NoNullBit;
        }

        var beneath = inPathOrder
            .Where(entry => entry.BeneathNullable)
            .Select(entry => (entry.Position, Above: nullBits[parents[entry.Position]]))
            .ToArray();

        // The fixed-width places from the end of the bitmap, then the descriptors, each in the order of the paths.
        var places = new StoredField[fields.Count];
        var elements = new StructureLayout?[fields.Count];
        var descriptors = new List<int>();
        var offset = start + RootLayout.BitBytes(nullableCount);
        var nested = false;
        var lists = false;

        foreach (var (position, _) in inPathOrder)
        {
            var (field, declaration) = fields[position];
            var (type, width) = TypeOf(entityType, field, declaration);

            nested |= declaration.Type is LatticeFieldTypeEnum.Owned or LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Reference;
            lists |= declaration.Type == LatticeFieldTypeEnum.List;

            if (type == StoredTypeEnum.Owned)
            {
                places[position] = new StoredField(field, declaration, type, 0, 0, nullBits[position], indexed: false);
            }
            else if (!IsDescriptor(type))
            {
                places[position] = new StoredField(field, declaration, type, width, offset, nullBits[position], indexedPositions.Contains(position));
                offset += width;
            }
        }

        var descriptorOffset = offset;

        foreach (var (position, _) in inPathOrder)
        {
            var (field, declaration) = fields[position];
            var (type, width) = TypeOf(entityType, field, declaration);

            if (IsDescriptor(type))
            {
                places[position] = new StoredField(field, declaration, type, width, offset, nullBits[position], indexedPositions.Contains(position));
                descriptors.Add(position);
                offset += width;

                if (type == StoredTypeEnum.List)
                {
                    elements[position] = OfElement(entityType, field, declaration);
                }
            }
        }

        return new StructureLayout(
            start,
            places,
            [.. fields.Select(member => member.Declaration)],
            [.. children],
            elements,
            [.. descriptors],
            beneath,
            nullableCount,
            descriptorOffset,
            offset - start,
            list,
            nested,
            lists);
    }

    // How the elements of a packed List of the element declared become bytes: by the .NET type a record holds
    // for the element, chosen here once, when the schema is compiled, so that no write and no read chooses it.
    private PackedElements PackedFor(LatticeValueDeclaration element) => element.ScalarType switch
    {
        LatticeFieldTypeEnum.Owned => new PackedOwnedValues(this),
        LatticeFieldTypeEnum.Boolean => ScalarsOf<bool>(element),
        LatticeFieldTypeEnum.Int8 => ScalarsOf<sbyte>(element),
        LatticeFieldTypeEnum.UInt8 => ScalarsOf<byte>(element),
        LatticeFieldTypeEnum.Int16 => ScalarsOf<short>(element),
        LatticeFieldTypeEnum.UInt16 => ScalarsOf<ushort>(element),
        LatticeFieldTypeEnum.Int32 => ScalarsOf<int>(element),
        LatticeFieldTypeEnum.UInt32 => ScalarsOf<uint>(element),
        LatticeFieldTypeEnum.Int64 => ScalarsOf<long>(element),
        LatticeFieldTypeEnum.UInt64 => ScalarsOf<ulong>(element),
        LatticeFieldTypeEnum.Float32 => ScalarsOf<float>(element),
        LatticeFieldTypeEnum.Float64 => ScalarsOf<double>(element),
        LatticeFieldTypeEnum.Decimal => ScalarsOf<decimal>(element),
        LatticeFieldTypeEnum.Guid => ScalarsOf<Guid>(element),
        LatticeFieldTypeEnum.DateTimeOffset => ScalarsOf<DateTimeOffset>(element),
        LatticeFieldTypeEnum.DateOnly => ScalarsOf<DateOnly>(element),
        LatticeFieldTypeEnum.TimeOnly => ScalarsOf<TimeOnly>(element),
        LatticeFieldTypeEnum.TimeSpan => ScalarsOf<TimeSpan>(element),
        _ => throw new UnreachableException($"An element of {element.Type} holds a descriptor, and its List lies as records."),
    };

    // The packed elements of a scalar element, of the type a record holds for it: the value alone, or, for a
    // nullable element, its null bit and the value at the place this layout gives it.
    private PackedElements ScalarsOf<TValue>(LatticeValueDeclaration element)
        where TValue : struct
    {
        Debug.Assert(typeof(TValue) == element.ValueType, "A record holds the element as the type its declaration names.");

        if (!element.IsNullable)
        {
            return new PackedScalars<TValue>(Size);
        }

        ref readonly var place = ref Place(0);

        return new PackedNullableScalars<TValue>(Size, Start + (place.NullBit / 8), (byte)(1 << (place.NullBit % 8)), place.Offset);
    }

    private static bool IsDescriptor(StoredTypeEnum type) => type is StoredTypeEnum.String or StoredTypeEnum.Binary or StoredTypeEnum.List;

    // The stored type and the width of a value of the declaration. A Map and a Set are not stored yet.
    private static (StoredTypeEnum Type, int Width) TypeOf(LatticeEntityType entityType, LatticeField field, LatticeValueDeclaration declaration) => declaration.ScalarType switch
    {
        LatticeFieldTypeEnum.Owned => (StoredTypeEnum.Owned, 0),
        LatticeFieldTypeEnum.List => (StoredTypeEnum.List, ValueBlocks.DescriptorSize),
        LatticeFieldTypeEnum.String => (StoredTypeEnum.String, ValueBlocks.DescriptorSize),
        LatticeFieldTypeEnum.Binary => (StoredTypeEnum.Binary, ValueBlocks.DescriptorSize),
        LatticeFieldTypeEnum.Map or LatticeFieldTypeEnum.Set =>
            throw new NotSupportedException($"The field {entityType.Name}.{field.Name} holds a {declaration.ScalarType}, which a root cannot store yet."),
        LatticeFieldTypeEnum.Boolean => (StoredTypeEnum.Boolean, 1),
        LatticeFieldTypeEnum.Float32 => (StoredTypeEnum.Float32, 4),
        LatticeFieldTypeEnum.Float64 => (StoredTypeEnum.Float64, 8),
        LatticeFieldTypeEnum.Decimal => (StoredTypeEnum.Decimal, 16),
        LatticeFieldTypeEnum.Guid => (StoredTypeEnum.Guid, 16),
        LatticeFieldTypeEnum.DateTimeOffset => (StoredTypeEnum.DateTimeOffset, 8),
        LatticeFieldTypeEnum.DateOnly => (StoredTypeEnum.DateOnly, 4),
        LatticeFieldTypeEnum.TimeOnly => (StoredTypeEnum.TimeOnly, 8),
        LatticeFieldTypeEnum.TimeSpan => (StoredTypeEnum.TimeSpan, 8),
        LatticeFieldTypeEnum.Int8 => (StoredTypeEnum.SignedInteger, 1),
        LatticeFieldTypeEnum.UInt8 => (StoredTypeEnum.UnsignedInteger, 1),
        LatticeFieldTypeEnum.Int16 => (StoredTypeEnum.SignedInteger, 2),
        LatticeFieldTypeEnum.UInt16 => (StoredTypeEnum.UnsignedInteger, 2),
        LatticeFieldTypeEnum.Int32 => (StoredTypeEnum.SignedInteger, 4),
        LatticeFieldTypeEnum.UInt32 => (StoredTypeEnum.UnsignedInteger, 4),
        LatticeFieldTypeEnum.Int64 => (StoredTypeEnum.SignedInteger, 8),
        LatticeFieldTypeEnum.UInt64 => (StoredTypeEnum.UnsignedInteger, 8),
        _ => throw new ArgumentOutOfRangeException(nameof(declaration), declaration.ScalarType, "Not a type a structure stores."),
    };
}
