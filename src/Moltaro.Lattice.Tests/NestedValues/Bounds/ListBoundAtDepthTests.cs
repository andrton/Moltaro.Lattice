namespace Moltaro.Lattice.Tests.NestedValues.Bounds;

// The bound of a List holds for a List at every depth, as for a field of the entity (ListBoundTests): an inner
// List of a List of Lists, a List in an owned value and a List in an owned value that is an element of a List.
// A List of exactly the bound passes and the write reaches its condition; one element more is ValidationFailed,
// by name and through the class, and when the entity is added to a write batch, before the conditions of the
// write, and the refusal names the type that declares the field and the field: Archive.Rows for an inner List
// of Rows, which Archive declares, and Shelf.Lines for the Lines of a Shelf wherever it lies.
//
// The schema: Archive, whose key Code is a String, with Rows, a List of Lists of String, Shelf, an owned Shelf,
// and Shelves, a List of Shelves; a Shelf holds Lines, a List of String. The bound of a List of String is
// 4,094 × (65,504 / (16 + 64)) = 3,348,892 elements, made of empty Strings, one object for every element. The
// class runs alone (LargeValueCollection).
[Collection<LargeValueCollection>]
public sealed class ListBoundAtDepthTests
{
    private const int MostStrings = 4_094 * (65_504 / (16 + 64));

    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Shelf", "ClrType": "{{typeof(Shelf).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Lines", "Type": "List", "Element": { "Type": "String" } } ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Archive", "ClrType": "{{typeof(Archive).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Code", "Type": "String" },
                { "Id": 2, "Name": "Rows", "Type": "List", "Element": { "Type": "List", "Element": { "Type": "String" } } },
                { "Id": 3, "Name": "Shelf", "Type": "Owned", "OwnedType": "Shelf" },
                { "Id": 4, "Name": "Shelves", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Shelf" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Fact]
    public async Task List_at_depth_of_its_bound_passes_the_validation_and_one_element_more_is_ValidationFailed_through_the_class()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var archives = database.Set<Archive>();
        var at = Empty(MostStrings);
        var above = Empty(MostStrings + 1);

        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => archives.ReplaceAsync(new Archive { Code = "absent", Rows = [[], at] }));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => archives.ReplaceAsync(new Archive { Code = "absent", Shelf = new Shelf { Lines = at } }));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => archives.ReplaceAsync(new Archive { Code = "absent", Shelves = [new Shelf(), new Shelf { Lines = at }] }));

        await TestData.ExpectValidationFailedAsync("Archive.Rows", () => archives.ReplaceAsync(new Archive { Code = "absent", Rows = [[], above] }));
        await TestData.ExpectValidationFailedAsync("Shelf.Lines", () => archives.ReplaceAsync(new Archive { Code = "absent", Shelf = new Shelf { Lines = above } }));
        await TestData.ExpectValidationFailedAsync("Shelf.Lines", () => archives.ReplaceAsync(new Archive { Code = "absent", Shelves = [new Shelf(), new Shelf { Lines = above }] }));

        // Added to a batch, as a write is.
        var batch = database.CreateWriteBatch();
        TestData.ExpectValidationFailed("Archive.Rows", () => batch.Insert(new Archive { Code = "above", Rows = [above] }));
        TestData.ExpectValidationFailed("Shelf.Lines", () => batch.Upsert(new Archive { Code = "above", Shelves = [new Shelf { Lines = above }] }));
        Assert.Equal(0, batch.Count);
    }

    [Fact]
    public async Task List_at_depth_above_its_bound_is_ValidationFailed_by_name_and_when_added_to_a_batch()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var archives = database.Set("Archive");
        var above = Empty(MostStrings + 1);

        Assert.True(Schema.TryGetOwnedType("Shelf", out var shelf));

        var record = new LatticeRecord(archives.EntityType)
        {
            ["Code"] = "absent",
            ["Rows"] = new List<List<string>>(),
            ["Shelf"] = new LatticeOwnedValue(shelf) { ["Lines"] = above },
            ["Shelves"] = new List<LatticeOwnedValue>(),
        };

        await TestData.ExpectValidationFailedAsync("Shelf.Lines", () => archives.ReplaceAsync(record));

        var batch = database.CreateWriteBatch();
        TestData.ExpectValidationFailed("Shelf.Lines", () => batch.Insert(record));
        Assert.Equal(0, batch.Count);

        record["Shelf"] = new LatticeOwnedValue(shelf) { ["Lines"] = Array.Empty<string>() };
        record["Rows"] = new List<List<string>> { new(), above };
        await TestData.ExpectValidationFailedAsync("Archive.Rows", () => archives.ReplaceAsync(record));
    }

    private static List<string> Empty(int count) => [.. Enumerable.Repeat("", count)];
}
