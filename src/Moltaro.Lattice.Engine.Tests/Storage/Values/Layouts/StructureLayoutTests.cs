using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Layouts;

// The stored form of a structure (StructureLayout): a null bitmap, the fixed-width fields and the descriptors
// that close it, for a root and for the record of an element of a List. The expected places are added up here
// from the rule, never read from the layout:
// - an owned value is flattened into its holder: its fixed-width fields stand among the fixed-width fields and
//   its descriptors among the descriptors, each group in the order of the paths of the field identities; a
//   nullable owned value has a presence bit, and every field beneath it a bit of its own, nullable or not;
// - a Reference is a field of the type of its target's key, at the width of that key, a String one a
//   descriptor;
// - a List is one descriptor, and the record of its element is the structure of the element declaration:
//   packed when it holds no descriptor, the List then bounded by its bytes, 4,094 chunks of 65,504; as
//   records when it does, 65,504 / (Size + 64 × descriptors) records a chunk, rounded down, and 4,094 such
//   chunks.
// Each entity type has the key Id, an Int64, after the 32 bytes of the block header.
public sealed class StructureLayoutTests
{
    private const string Point = "{ 'Id': 2, 'Name': 'Point', 'Fields': [ { 'Id': 1, 'Name': 'Lat', 'Type': 'Float64' }, { 'Id': 2, 'Name': 'Lon', 'Type': 'Float64' } ] }";

    // City, a String; Zip, an Int32; Note, a nullable String; Geo, a nullable owned Point.
    private const string Address = """
        { 'Id': 1, 'Name': 'Address', 'Fields': [
          { 'Id': 1, 'Name': 'City', 'Type': 'String' },
          { 'Id': 2, 'Name': 'Zip', 'Type': 'Int32' },
          { 'Id': 3, 'Name': 'Note', 'Type': 'String', 'Nullable': true },
          { 'Id': 4, 'Name': 'Geo', 'Type': 'Owned', 'OwnedType': 'Point', 'Nullable': true } ] }
        """;

    [Fact]
    public void Owned_values_are_flattened_into_the_root_in_the_order_of_the_paths_with_a_bit_for_every_field_beneath_a_nullable_one()
    {
        // Item: Id; Home, a nullable Address; Count, an Int16; Work, an Address that is not nullable.
        var layout = LayoutOf(
            "{ 'Id': 2, 'Name': 'Home', 'Type': 'Owned', 'OwnedType': 'Address', 'Nullable': true }, { 'Id': 3, 'Name': 'Count', 'Type': 'Int16' }, { 'Id': 4, 'Name': 'Work', 'Type': 'Owned', 'OwnedType': 'Address' }",
            out var item);

        // The bits, in the order of the paths: Home 0, its City 1, Zip 2, Note 3, Geo 4, Lat 5, Lon 6, all
        // beneath the nullable Home; of Work only the nullable Note 7, Geo 8 and, beneath it, Lat 9 and Lon 10.
        // 11 bits, 2 bytes. The fixed fields from 32 + 2 = 34: Id 8, Home.Zip 4, Home.Geo.Lat 8, Home.Geo.Lon
        // 8, Count 2, Work.Zip 4, Work.Geo.Lat 8, Work.Geo.Lon 8, to 84; then the descriptors of Home.City,
        // Home.Note, Work.City and Work.Note, 16 each, to 148.
        Assert.Equal(148, layout.FixedSize);

        ExpectField(layout, item, "Id", StoredTypeEnum.SignedInteger, offset: 34, width: 8, nullBit: StoredField.NoNullBit);
        ExpectOwned(layout, item, "Home", nullBit: 0);
        ExpectField(layout, item, "Home.City", StoredTypeEnum.String, offset: 84, width: 16, nullBit: 1);
        ExpectField(layout, item, "Home.Zip", StoredTypeEnum.SignedInteger, offset: 42, width: 4, nullBit: 2);
        ExpectField(layout, item, "Home.Note", StoredTypeEnum.String, offset: 100, width: 16, nullBit: 3);
        ExpectOwned(layout, item, "Home.Geo", nullBit: 4);
        ExpectField(layout, item, "Home.Geo.Lat", StoredTypeEnum.Float64, offset: 46, width: 8, nullBit: 5);
        ExpectField(layout, item, "Home.Geo.Lon", StoredTypeEnum.Float64, offset: 54, width: 8, nullBit: 6);
        ExpectField(layout, item, "Count", StoredTypeEnum.SignedInteger, offset: 62, width: 2, nullBit: StoredField.NoNullBit);
        ExpectOwned(layout, item, "Work", nullBit: StoredField.NoNullBit);
        ExpectField(layout, item, "Work.City", StoredTypeEnum.String, offset: 116, width: 16, nullBit: StoredField.NoNullBit);
        ExpectField(layout, item, "Work.Zip", StoredTypeEnum.SignedInteger, offset: 64, width: 4, nullBit: StoredField.NoNullBit);
        ExpectField(layout, item, "Work.Note", StoredTypeEnum.String, offset: 132, width: 16, nullBit: 7);
        ExpectOwned(layout, item, "Work.Geo", nullBit: 8);
        ExpectField(layout, item, "Work.Geo.Lat", StoredTypeEnum.Float64, offset: 68, width: 8, nullBit: 9);
        ExpectField(layout, item, "Work.Geo.Lon", StoredTypeEnum.Float64, offset: 76, width: 8, nullBit: 10);
    }

    [Fact]
    public void A_reference_is_a_field_of_the_type_of_its_targets_key()
    {
        // Item: Id; Parent, a nullable reference to Item, whose key is an Int64; Owner, to Named, whose key is a
        // String; Token, to Tokened, whose key is a Guid; Small, to Small, whose key is a UInt16. One bit, for
        // Parent: 1 byte. The fixed fields from 33: Id 8, Parent 8, Token 16, Small 2, to 67; the descriptor of
        // Owner, to 83.
        var layout = LayoutOf(
            "{ 'Id': 2, 'Name': 'Parent', 'Type': 'Reference', 'EntityType': 'Item', 'Nullable': true }, { 'Id': 3, 'Name': 'Owner', 'Type': 'Reference', 'EntityType': 'Named' }, { 'Id': 4, 'Name': 'Token', 'Type': 'Reference', 'EntityType': 'Tokened' }, { 'Id': 5, 'Name': 'Small', 'Type': 'Reference', 'EntityType': 'Small' }",
            out var item);

        Assert.Equal(83, layout.FixedSize);

        ExpectField(layout, item, "Parent", StoredTypeEnum.SignedInteger, offset: 41, width: 8, nullBit: 0);
        ExpectField(layout, item, "Owner", StoredTypeEnum.String, offset: 67, width: 16, nullBit: StoredField.NoNullBit);
        ExpectField(layout, item, "Token", StoredTypeEnum.Guid, offset: 49, width: 16, nullBit: StoredField.NoNullBit);
        ExpectField(layout, item, "Small", StoredTypeEnum.UnsignedInteger, offset: 65, width: 2, nullBit: StoredField.NoNullBit);
    }

    [Fact]
    public void A_List_is_one_descriptor_and_the_record_of_its_element_is_the_structure_of_the_element()
    {
        var layout = LayoutOf(
            """
            { 'Id': 2, 'Name': 'Tags', 'Type': 'List', 'Element': { 'Type': 'String' } },
            { 'Id': 3, 'Name': 'Scores', 'Type': 'List', 'Element': { 'Type': 'Int64' } },
            { 'Id': 4, 'Name': 'Marks', 'Type': 'List', 'Element': { 'Type': 'Int32', 'Nullable': true } },
            { 'Id': 5, 'Name': 'Notes', 'Type': 'List', 'Element': { 'Type': 'String', 'Nullable': true } },
            { 'Id': 6, 'Name': 'Places', 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Address' } },
            { 'Id': 7, 'Name': 'Pins', 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Point' } },
            { 'Id': 8, 'Name': 'Grid', 'Type': 'List', 'Element': { 'Type': 'List', 'Element': { 'Type': 'Int32' } } },
            { 'Id': 9, 'Name': 'Parents', 'Type': 'List', 'Element': { 'Type': 'Reference', 'EntityType': 'Item' } },
            { 'Id': 10, 'Name': 'Owners', 'Type': 'List', 'Element': { 'Type': 'Reference', 'EntityType': 'Named' } }
            """,
            out var item);

        // 32 + Id 8 + nine descriptors of 16.
        Assert.Equal(32 + 8 + (9 * 16), layout.FixedSize);
        ExpectField(layout, item, "Tags", StoredTypeEnum.List, offset: 40, width: 16, nullBit: StoredField.NoNullBit);
        ExpectField(layout, item, "Owners", StoredTypeEnum.List, offset: 40 + (8 * 16), width: 16, nullBit: StoredField.NoNullBit);

        // Packed: the stride, and 268,173,376 bytes divided by it, rounded down.
        ExpectElement(layout, item, "Scores", size: 8, descriptors: 0, recordsPerChunk: 0, mostElements: 33_521_672);
        ExpectElement(layout, item, "Marks", size: 1 + 4, descriptors: 0, recordsPerChunk: 0, mostElements: 53_634_675);
        ExpectElement(layout, item, "Pins", size: 8 + 8, descriptors: 0, recordsPerChunk: 0, mostElements: 16_760_836);
        ExpectElement(layout, item, "Parents", size: 8, descriptors: 0, recordsPerChunk: 0, mostElements: 33_521_672);

        // As records: 65,504 / (Size + 64 × descriptors), rounded down, records a chunk, and 4,094 chunks.
        // A String element: 65,504 / 80 = 818. A nullable one, a bitmap of one byte before it: 65,504 / 81 =
        // 808. A List element, one descriptor: 818. An Address: a bitmap of one byte for Note, Geo and Geo's Lat
        // and Lon; Zip 4, Lat 8, Lon 8; the descriptors of City and Note: 1 + 20 + 32 = 53 bytes, and
        // 65,504 / (53 + 128) = 361.
        ExpectElement(layout, item, "Tags", size: 16, descriptors: 1, recordsPerChunk: 818, mostElements: 4_094 * 818);
        ExpectElement(layout, item, "Notes", size: 1 + 16, descriptors: 1, recordsPerChunk: 808, mostElements: 4_094 * 808);
        ExpectElement(layout, item, "Grid", size: 16, descriptors: 1, recordsPerChunk: 818, mostElements: 4_094 * 818);
        ExpectElement(layout, item, "Places", size: 1 + 20 + 32, descriptors: 2, recordsPerChunk: 361, mostElements: 4_094 * 361);
        ExpectElement(layout, item, "Owners", size: 16, descriptors: 1, recordsPerChunk: 818, mostElements: 4_094 * 818);
    }

    [Fact]
    public void The_descriptors_of_a_root_with_owned_values_and_Lists_are_one_run_after_its_fixed_fields()
    {
        // Item: Id; Home, a nullable Address; Tags, a List of String. The fixed fields: Id, Home.Zip,
        // Home.Geo.Lat, Home.Geo.Lon; then the descriptors of Home.City, Home.Note and Tags, in that order.
        var layout = LayoutOf(
            "{ 'Id': 2, 'Name': 'Home', 'Type': 'Owned', 'OwnedType': 'Address', 'Nullable': true }, { 'Id': 3, 'Name': 'Tags', 'Type': 'List', 'Element': { 'Type': 'String' } }",
            out var item);

        // Seven bits, one byte; 32 + 1 + 8 + 4 + 8 + 8 = 61, where the descriptors begin.
        Assert.Equal(61, layout.DescriptorOffset);
        Assert.Equal(3, layout.DescriptorCount);
        Assert.Equal(61 + (3 * 16), layout.FixedSize);
        Assert.Equal(StoredTypeEnum.String, layout.DescriptorField(0).Type);
        Assert.Equal(StoredTypeEnum.String, layout.DescriptorField(1).Type);
        Assert.Equal(StoredTypeEnum.List, layout.DescriptorField(2).Type);
        Assert.Same(item.Fields.Single(field => field.Name == "Tags"), layout.DescriptorField(2).Field);
    }

    private static RootLayout LayoutOf(string fields, out LatticeEntityType item)
    {
        const string Targets = """
            ,
            { "Id": 2, "Name": "Named", "Fields": [ { "Id": 1, "Name": "Id", "Type": "String" } ], "PrimaryKey": [1] },
            { "Id": 3, "Name": "Tokened", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Guid" } ], "PrimaryKey": [1] },
            { "Id": 4, "Name": "Small", "Fields": [ { "Id": 1, "Name": "Id", "Type": "UInt16" } ], "PrimaryKey": [1] }
            """;

        // Item and the targets of its references, beside it in one schema.
        var json = ItemSchema.Json(fields, ownedTypes: $"{Address}, {Point}");
        var schema = LatticeSchema.Compile(json.Insert(json.LastIndexOf(']'), Targets));

        Assert.True(schema.TryGetEntityType(ItemSchema.EntityTypeName, out var entityType));

        item = entityType;

        return RootLayout.Compile(entityType, schema);
    }

    // The fields from the entity type down to the field, as LatticeIndexPart.Path gives them.
    private static LatticeField[] PathOf(LatticeEntityType item, string dotted)
    {
        var names = dotted.Split('.');
        var path = new List<LatticeField>();

        Assert.True(item.TryGetField(names[0], out var first));
        path.Add(first);

        foreach (var name in names[1..])
        {
            Assert.True(path[^1].Declaration.OwnedType!.TryGetField(name, out var next));
            path.Add(next);
        }

        return [.. path];
    }

    private static void ExpectField(RootLayout layout, LatticeEntityType item, string path, StoredTypeEnum type, int offset, int width, int nullBit)
    {
        var stored = layout.StoredFieldOf(PathOf(item, path));

        Assert.Equal((type, offset, width, nullBit), (stored.Type, stored.Offset, stored.Width, stored.NullBit));
    }

    // An owned value has no bytes of its own; a nullable one has its presence bit.
    private static void ExpectOwned(RootLayout layout, LatticeEntityType item, string path, int nullBit)
    {
        var stored = layout.StoredFieldOf(PathOf(item, path));

        Assert.Equal((StoredTypeEnum.Owned, 0, nullBit), (stored.Type, stored.Width, stored.NullBit));
    }

    private static void ExpectElement(RootLayout layout, LatticeEntityType item, string list, int size, int descriptors, int recordsPerChunk, int mostElements)
    {
        var element = layout.ElementOf(layout.StoredFieldOf(PathOf(item, list)));

        Assert.Equal((size, descriptors, descriptors > 0, recordsPerChunk, mostElements), (element.Size, element.DescriptorCount, element.HoldsDescriptors, element.RecordsPerChunk, element.MostElements));
    }
}
