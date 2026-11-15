namespace Moltaro.Lattice.Tests.Indexes;

// The field a part of an index ends at, which for a plain part is the field itself, is one of
// three: a scalar that is not Binary, an Enum, a Reference. A Reference is compared as the key
// of its target, so a search key gives it as that key. A part that ends at an owned value, a
// List, a Map or a Set is refused with the index and the field named: an owned value is
// indexed through a path to a field inside it, and the members of a collection are not
// indexed. A part of a search key, a value given by itself, accepts an integer of another
// width when it fits.
public sealed class IndexPartTypeTests
{
    private const string LastIndex = "{ 'Id': 7, 'Name': 'ByLogin', 'Fields': [10] }";

    [Fact]
    public void Index_part_may_end_at_a_scalar_an_Enum_or_a_Reference_plain_or_inside_an_owned_value()
    {
        var person = Assert.Single(PartSchema().EntityTypes);

        Assert.Equal(LatticeFieldTypeEnum.Reference, PartField(person, "ByManager").Declaration.Type);
        Assert.Equal(LatticeFieldTypeEnum.Reference, PartField(person, "ByMentor").Declaration.Type);
        Assert.Equal(LatticeFieldTypeEnum.Enum, PartField(person, "ByLevel").Declaration.Type);
        Assert.Equal(LatticeFieldTypeEnum.String, PartField(person, "ByLogin").Declaration.Type);
        Assert.Equal(LatticeFieldTypeEnum.Reference, PartField(person, "ByWorkOwner").Declaration.Type);
        Assert.Equal(LatticeFieldTypeEnum.Enum, PartField(person, "ByWorkRank").Declaration.Type);
        Assert.Equal(LatticeFieldTypeEnum.Int16, PartField(person, "ByWorkFloor").Declaration.Type);
    }

    [Theory]
    // An owned value, plain, as a list of one identity, and at the end of a path.
    [InlineData("[2]", "Home")]
    [InlineData("[3]", "Work")]
    [InlineData("[ [2] ]", "Home")]
    [InlineData("[ [3, 6] ]", "Location")]
    // A List, a Map, a Set, plain and at the end of a path.
    [InlineData("[7]", "Tags")]
    [InlineData("[8]", "Options")]
    [InlineData("[9]", "Rights")]
    [InlineData("[ [3, 5] ]", "Lines")]
    // Behind a field the index may have.
    [InlineData("[10, 7]", "Tags")]
    public void Index_part_that_ends_at_an_owned_value_or_a_collection_is_refused_with_the_index_and_the_field_named(string fields, string field)
    {
        var exception = Descriptor.ExpectRefused(PartJson, LastIndex, $"{LastIndex}, {{ 'Id': 8, 'Name': 'ByPart', 'Fields': {fields} }}");

        Assert.Contains("ByPart", exception.Message);
        Assert.Contains(field, exception.Message);
    }

    [Fact]
    public async Task Search_key_gives_a_reference_as_the_key_of_its_target()
    {
        await using var database = await TestData.OpenMasterAsync(schema: PartSchema());

        foreach (var byMentor in new[] { database.Set("Person").Index("ByMentor"), (object)database.Set<Person>().Index("ByMentor") })
        {
            // Mentor refers to Person, whose key is an Int64. The key is accepted, and the
            // search, which is not implemented, is refused after it.
            ExpectAccepted(byMentor, IndexKey.Equal(7L));
            ExpectAccepted(byMentor, IndexKey.Equal(7));
            ExpectRefused(byMentor, IndexKey.Equal("seven"));
            ExpectRefused(byMentor, IndexKey.Equal(Guid.Empty));

            // Mentor is never null.
            ExpectRefused(byMentor, IndexKey.Equal<long?>(null));
        }

        // Manager is nullable, and so is the Owner of an Address.
        ExpectAccepted(database.Set("Person").Index("ByManager"), IndexKey.Equal<long?>(null));
        ExpectAccepted(database.Set("Person").Index("ByWorkOwner"), IndexKey.Equal<long?>(null));
        ExpectAccepted(database.Set("Person").Index("ByWorkOwner"), IndexKey.Equal(7L));
    }

    [Fact]
    public async Task Search_key_gives_an_Enum_part_as_its_underlying_integer()
    {
        await using var database = await TestData.OpenMasterAsync(schema: PartSchema());

        ExpectAccepted(database.Set("Person").Index("ByLevel"), IndexKey.Equal(1));
        ExpectAccepted(database.Set("Person").Index("ByWorkRank"), IndexKey.Equal(1));
        ExpectRefused(database.Set("Person").Index("ByWorkRank"), IndexKey.Equal("High"));
    }

    [Fact]
    public async Task Part_of_a_search_key_accepts_an_integer_of_another_width_when_it_fits()
    {
        await using var database = await TestData.OpenMasterAsync(schema: PartSchema());
        var byWorkFloor = database.Set<Person>().Index("ByWorkFloor");

        // Floor is an Int16 inside the owned value Work.
        ExpectAccepted(byWorkFloor, IndexKey.Equal((short)3));
        ExpectAccepted(byWorkFloor, IndexKey.Equal(3));
        ExpectAccepted(byWorkFloor, IndexKey.Equal(3L));
        ExpectRefused(byWorkFloor, IndexKey.Equal(70_000));
    }

    // The key is accepted: the search that follows is refused as not implemented.
    private static void ExpectAccepted(object index, IndexKey key) =>
        Assert.Throws<NotSupportedException>(() => Find(index, key));

    private static void ExpectRefused(object index, IndexKey key) =>
        Assert.Throws<ArgumentException>(() => Find(index, key));

    private static List<object> Find(object index, IndexKey key) => index switch
    {
        ILatticeIndex<LatticeRecord> records => [.. records.Find(key)],
        ILatticeIndex<Person> people => [.. people.Find(key)],
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    private static LatticeField PartField(LatticeEntityType entityType, string indexName)
    {
        Assert.True(entityType.TryGetIndex(indexName, out var index));
        return Assert.Single(index.Parts).Field;
    }

    private static LatticeSchema PartSchema() => LatticeSchema.Compile(PartJson);

    // Person has owned values, references, an Enum and collections; every index it declares
    // ends at a field an index may have.
    private static string PartJson => $$"""
        {
          "Enums": [ { "Id": 1, "Name": "Level", "Members": [ { "Name": "Low", "Value": 0 }, { "Name": "High", "Value": 1 } ] } ],
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Address", "ClrType": "{{typeof(Address).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "City", "Type": "String" },
                { "Id": 2, "Name": "Floor", "Type": "Int16" },
                { "Id": 3, "Name": "Rank", "Type": "Enum", "Enum": "Level" },
                { "Id": 4, "Name": "Owner", "Type": "Reference", "EntityType": "Person", "Nullable": true },
                { "Id": 5, "Name": "Lines", "Type": "List", "Element": { "Type": "String" } },
                { "Id": 6, "Name": "Location", "Type": "Owned", "OwnedType": "Point", "Nullable": true }
              ]
            },
            {
              "Id": 2, "Name": "Point", "ClrType": "{{typeof(Point).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Lat", "Type": "Float64" } ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Person", "ClrType": "{{typeof(Person).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Home", "Type": "Owned", "OwnedType": "Address", "Nullable": true },
                { "Id": 3, "Name": "Work", "Type": "Owned", "OwnedType": "Address" },
                { "Id": 4, "Name": "Manager", "Type": "Reference", "EntityType": "Person", "Nullable": true },
                { "Id": 5, "Name": "Mentor", "Type": "Reference", "EntityType": "Person" },
                { "Id": 6, "Name": "Level", "Type": "Enum", "Enum": "Level" },
                { "Id": 7, "Name": "Tags", "Type": "List", "Element": { "Type": "String" } },
                { "Id": 8, "Name": "Options", "Type": "Map", "Key": { "Type": "String" }, "Value": { "Type": "String" } },
                { "Id": 9, "Name": "Rights", "Type": "Set", "Element": { "Type": "String" } },
                { "Id": 10, "Name": "Login", "Type": "String" }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByManager", "Fields": [4] },
                { "Id": 2, "Name": "ByMentor", "Fields": [5] },
                { "Id": 3, "Name": "ByLevel", "Fields": [6] },
                { "Id": 4, "Name": "ByWorkOwner", "Fields": [ [3, 4] ] },
                { "Id": 5, "Name": "ByWorkRank", "Fields": [ [3, 3] ] },
                { "Id": 6, "Name": "ByWorkFloor", "Fields": [ [3, 2] ] },
                {{LastIndex.Replace('\'', '"')}}
              ]
            }
          ]
        }
        """;

    private sealed class Person
    {
        public long Id { get; set; }

        public Address? Home { get; set; }

        public Address Work { get; set; } = new();

        public long? Manager { get; set; }

        public long Mentor { get; set; }

        public int Level { get; set; }

        public List<string> Tags { get; set; } = [];

        public Dictionary<string, string> Options { get; set; } = [];

        public HashSet<string> Rights { get; set; } = [];

        public string Login { get; set; } = "";
    }

    private sealed class Address
    {
        public string City { get; set; } = "";

        public short Floor { get; set; }

        public int Rank { get; set; }

        public long? Owner { get; set; }

        public List<string> Lines { get; set; } = [];

        public Point? Location { get; set; }
    }

    private sealed class Point
    {
        public double Lat { get; set; }
    }
}
