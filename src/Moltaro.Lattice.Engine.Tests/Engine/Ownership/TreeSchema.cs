using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// The schema of the tests of ownership: Item, whose key Id is an Int64, with a value of every form the engine
// stores outside the root block, at every depth it can lie: Name, a String with Dictionary storage; Home, a
// nullable owned Address, whose City has Dictionary storage, whose Lines is a List of String and whose Geo a
// nullable owned Point; Tags, a List of String; Scores, a List of Int64; Places, a List of owned Addresses;
// Grid, a List of Lists of Int32; Photo, a nullable Binary; Files, a List of Binary. It declares no secondary
// index, so that every block of the arena belongs to a root or to an entry of the text table (OwnershipAudit).
//
// The trees of the tests come in shapes, each a number: 0 is bare, with nothing beside its Name; 1 small, every
// value inline or in one block; 2 with every List in chunks of both forms, values above 64 bytes in blocks of
// their own inside records and a Binary above a chunk inside a record; 3 large, a List of 750,000 Int64, about
// 6 MB in chunks, which a small arena refuses; 4 texts above a chunk at every depth; 5 owned values that share
// their Dictionary texts. The values of a shape depend on the number given with it, so that a replace by the
// same shape changes values and not only places.
internal static class TreeSchema
{
    public const int Shapes = 6;

    public static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Address",
              "Fields": [
                { "Id": 1, "Name": "City", "Type": "String", "StringStorage": "Dictionary" },
                { "Id": 2, "Name": "Lines", "Type": "List", "Element": { "Type": "String" } },
                { "Id": 3, "Name": "Geo", "Type": "Owned", "OwnedType": "Point", "Nullable": true }
              ]
            },
            { "Id": 2, "Name": "Point", "Fields": [ { "Id": 1, "Name": "Lat", "Type": "Float64" }, { "Id": 2, "Name": "Lon", "Type": "Float64" } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Name", "Type": "String", "StringStorage": "Dictionary" },
                { "Id": 3, "Name": "Home", "Type": "Owned", "OwnedType": "Address", "Nullable": true },
                { "Id": 4, "Name": "Tags", "Type": "List", "Element": { "Type": "String" } },
                { "Id": 5, "Name": "Scores", "Type": "List", "Element": { "Type": "Int64" } },
                { "Id": 6, "Name": "Places", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Address" } },
                { "Id": 7, "Name": "Grid", "Type": "List", "Element": { "Type": "List", "Element": { "Type": "Int32" } } },
                { "Id": 8, "Name": "Photo", "Type": "Binary", "Nullable": true },
                { "Id": 9, "Name": "Files", "Type": "List", "Element": { "Type": "Binary" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    // A record of Item with the key and the tree of the shape, its values made from the number.
    public static LatticeRecord RecordOf(ILatticeSet items, long id, int shape, int number)
    {
        Assert.True(Schema.TryGetOwnedType("Address", out var address));
        Assert.True(Schema.TryGetOwnedType("Point", out var point));

        var record = new LatticeRecord(items.EntityType)
        {
            ["Id"] = id,
            ["Name"] = $"name {number % 3}",
            ["Tags"] = Array.Empty<string>(),
            ["Scores"] = Array.Empty<long>(),
            ["Places"] = Array.Empty<LatticeOwnedValue>(),
            ["Grid"] = Array.Empty<List<int>>(),
            ["Files"] = Array.Empty<byte[]>(),
        };

        LatticeOwnedValue AddressOf(string city, string[] lines, bool withGeo) => new(address)
        {
            ["City"] = city,
            ["Lines"] = lines,
            ["Geo"] = withGeo ? new LatticeOwnedValue(point) { ["Lat"] = (double)number, ["Lon"] = -(double)number } : null,
        };

        switch (shape)
        {
            case 0:
                break;

            case 1:
                record["Home"] = AddressOf($"city {number}", ["a line"], withGeo: true);
                record["Tags"] = new[] { "one", $"tag {number}" };
                record["Scores"] = new long[] { number, 2, 3 };
                record["Places"] = new[] { AddressOf("Oslo", [], withGeo: false) };
                record["Grid"] = new[] { new List<int> { number } };
                record["Photo"] = new byte[] { (byte)number };
                record["Files"] = new[] { new byte[] { 1, 2 } };
                break;

            case 2:
                record["Home"] = AddressOf(new string('h', 70 + (number % 5)), [.. Enumerable.Range(0, 900).Select(index => Text(index, number))], withGeo: false);
                record["Tags"] = Enumerable.Range(0, 900).Select(index => Text(index, number)).ToArray();
                record["Scores"] = Enumerable.Range(0, 9_000).Select(index => (long)index * number).ToArray();
                record["Places"] = Enumerable.Range(0, 400).Select(index => AddressOf(Text(index, number), [Text(index + 1, number)], withGeo: index % 2 == 0)).ToArray();
                record["Grid"] = Enumerable.Range(0, 20).Select(index => Enumerable.Repeat(index + number, 600).ToList()).ToArray();
                record["Photo"] = new byte[5_000];
                record["Files"] = new[] { new byte[70_000], new byte[65], Array.Empty<byte>() };
                break;

            case 3:
                record["Scores"] = new long[750_000];
                break;

            case 4:
                var longText = new string('x', 70_000 + number);
                record["Home"] = AddressOf(longText, [longText, "short"], withGeo: true);
                record["Tags"] = new[] { longText, longText, "" };
                record["Places"] = new[] { AddressOf(longText, [longText], withGeo: false) };
                record["Photo"] = new byte[70_000];
                break;

            default:
                record["Home"] = AddressOf("shared", [], withGeo: false);
                record["Places"] = Enumerable.Range(0, 30).Select(index => AddressOf(index % 2 == 0 ? "shared" : new string('s', 100), [], withGeo: false)).ToArray();
                break;
        }

        return record;
    }

    // A text of 20 to 99 bytes, so that some lie inline in their records and some in blocks of their own.
    private static string Text(int index, int number) => $"{index:D4} {number}".PadRight(20 + ((index * 7) % 80), '.');
}
