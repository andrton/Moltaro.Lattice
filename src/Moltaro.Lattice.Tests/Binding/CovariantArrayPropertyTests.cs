namespace Moltaro.Lattice.Tests.Binding;

// An array property of a bound class may hold, at run time, an array of a class derived from
// its element type: by covariance every element really is of the declared type. Such an object
// is valid when it is written, in the class of an entity type as in the class of an owned type
// it holds, and so is an array of the declared type that holds objects of a derived class. An
// array whose elements would be read as other values is not, see ArrayPropertyTests.
public sealed class CovariantArrayPropertyTests
{
    [Fact]
    public async Task Array_properties_that_hold_arrays_of_derived_classes_are_valid_when_written()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Schema());
        var holder = new Holder
        {
            Id = 1,
            Places = new SpecialPlace[] { new() { Number = 1 } },
            Layers = new LongList[] { [1, 2] },
            Box = new Box { Places = new SpecialPlace[] { new() { Number = 2 } }, Layers = new LongList[] { [3] } },
        };

        // Validated and taken, and written.
        var batch = database.CreateWriteBatch();
        batch.Insert(holder);
        Assert.Equal(1, batch.Count);
        Assert.True((await database.Set<Holder>().InsertAsync(holder)).Changed);
    }

    [Fact]
    public async Task Array_of_the_declared_class_that_holds_objects_of_a_derived_one_is_valid_when_written()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Schema());
        var holder = new Holder
        {
            Id = 1,
            Places = [new SpecialPlace { Number = 1 }, new Place { Number = 2 }],
            Layers = [new LongList { 1 }, [2]],
            Box = new Box { Places = [new SpecialPlace()], Layers = [new LongList()] },
        };

        var batch = database.CreateWriteBatch();
        batch.Insert(holder);
        Assert.Equal(1, batch.Count);
    }

    // Holder holds Places, a List of the owned type Place, Layers, a List of Lists of Int64, and
    // Box, an owned value whose class has the same two.
    private static LatticeSchema Schema() => LatticeSchema.Compile($$"""
        {
          "OwnedTypes": [
            { "Id": 1, "Name": "Place", "ClrType": "{{typeof(Place).FullName}}", "Fields": [ { "Id": 1, "Name": "Number", "Type": "Int32" } ] },
            {
              "Id": 2, "Name": "Box", "ClrType": "{{typeof(Box).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Places", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Place" } },
                { "Id": 2, "Name": "Layers", "Type": "List", "Element": { "Type": "List", "Element": { "Type": "Int64" } } }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Holder", "ClrType": "{{typeof(Holder).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Places", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Place" } },
                { "Id": 3, "Name": "Layers", "Type": "List", "Element": { "Type": "List", "Element": { "Type": "Int64" } } },
                { "Id": 4, "Name": "Box", "Type": "Owned", "OwnedType": "Box" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private sealed class Holder
    {
        public long Id { get; set; }

        public Place[] Places { get; set; } = [];

        public List<long>[] Layers { get; set; } = [];

        public Box Box { get; set; } = new();
    }

    private sealed class Box
    {
        public Place[] Places { get; set; } = [];

        public List<long>[] Layers { get; set; } = [];
    }

    private class Place
    {
        public int Number { get; set; }
    }

    private sealed class SpecialPlace : Place;

    private sealed class LongList : List<long>;
}
