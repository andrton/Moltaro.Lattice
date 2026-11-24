using System.Runtime.InteropServices;
using Moltaro.Lattice.TestSupport.PartialUpdates;

namespace Moltaro.Lattice.Tests.NestedValues.Bounds;

// A List has the bound that one directory of chunks gives it, a fixed rule of the storage format as the bound
// of one String or Binary value is, and a List above it is refused as a String above its bound is:
// ValidationFailed with the field named, before the conditions of the write are looked at. A List of
// exactly the bound passes the validation, and the write reaches its condition. The figures come from the
// layout: one directory names 4,094 chunks (ValueBlocks.MostChunks) of 65,504 bytes each.
// - Packed, a List whose elements hold no descriptor lies as the bytes of a Binary value, so it holds at
//   most 4,094 × 65,504 = 268,173,376 bytes: Values, of Int64 at a stride of 8, holds 268,173,376 / 8 =
//   33,521,672 elements.
// - As records, a List whose elements hold descriptors holds 65,504 / (16 + 64) = 818 records of a String in
//   a chunk, rounded down, so Labels holds 4,094 × 818 = 3,348,892 elements.
// The schema: Series, whose key Code is a String, with Values, a List of Int64, and Labels, a List of String.
// The Lists are written through the class, whose write copies them once; the Strings are empty, one object
// for every element. The class runs alone (LargeValueCollection).
[Collection<LargeValueCollection>]
public sealed class ListBoundTests
{
    private const int MostInt64 = 4_094 * 65_504 / 8;

    private const int MostStrings = 4_094 * (65_504 / (16 + 64));

    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Series", "ClrType": "{{typeof(Series).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Code", "Type": "String" },
                { "Id": 2, "Name": "Values", "Type": "List", "Element": { "Type": "Int64" } },
                { "Id": 3, "Name": "Labels", "Type": "List", "Element": { "Type": "String" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Fact]
    public async Task Packed_List_of_its_bound_passes_the_validation_and_one_element_more_is_ValidationFailed()
    {
        Assert.Equal(33_521_672, MostInt64);

        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var series = database.Set<Series>();

        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => series.ReplaceAsync(new Series { Code = "absent", Values = Zeros(MostInt64) }));
        await TestData.ExpectValidationFailedAsync("Values", () => series.ReplaceAsync(new Series { Code = "absent", Values = Zeros(MostInt64 + 1) }));
    }

    [Fact]
    public async Task List_of_records_of_its_bound_passes_the_validation_and_one_element_more_is_ValidationFailed()
    {
        Assert.Equal(3_348_892, MostStrings);

        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var series = database.Set<Series>();

        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => series.ReplaceAsync(new Series { Code = "absent", Labels = Empty(MostStrings) }));
        await TestData.ExpectValidationFailedAsync("Labels", () => series.ReplaceAsync(new Series { Code = "absent", Labels = Empty(MostStrings + 1) }));

        // Added to a batch, as a write is.
        var batch = database.CreateWriteBatch();
        TestData.ExpectValidationFailed("Labels", () => batch.Insert(new Series { Code = "above", Labels = Empty(MostStrings + 1) }));
        Assert.Equal(0, batch.Count);
    }

    [Fact]
    public async Task Editing_a_List_at_its_count_limit_fails_at_execution_and_preserves_its_revision()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var series = database.Set<Series>();
        await series.InsertAsync(new Series { Code = "series", Values = [4], Labels = Empty(MostStrings) });
        Assert.True(series.TryGet("series", out _, out var revision));
        var before = database.Position;

        foreach (var inserts in new[] { false, true })
        {
            var update = series.CreateUpdate("series");
            var prior = update.ListReplace("Values", 0, 17L);
            var removed = update.ListRemove("Labels", 0);
            var restored = update.ListAppend("Labels", "at the bound");
            var added = inserts
                ? update.ListInsert("Labels", MostStrings, "one too many")
                : update.ListAppend("Labels", "one too many");
            var batch = database.CreateWriteBatch();
            batch.Update(update, revision);
            var error = await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => batch.CommitAsync());

            Assert.Equal(0, error.OperationIndex);
            Assert.Contains($"would hold {MostStrings + 1} entries", error.Message);
            Assert.Throws<InvalidOperationException>(() => prior.Changed);
            Assert.Throws<InvalidOperationException>(() => removed.Changed);
            Assert.Throws<InvalidOperationException>(() => restored.Changed);
            Assert.Throws<InvalidOperationException>(() => added.Changed);
            Assert.Equal(before, database.Position);
            Assert.True(series.TryGet("series", out var stored, out var after));
            Assert.Equal(revision, after);
            Assert.Equal(4L, Assert.Single(stored.Values));
            Assert.Equal(MostStrings, stored.Labels.Count);
            Assert.Equal("", stored.Labels[^1]);
        }
    }

    // A List of count zeros, made without a loop.
    private static List<long> Zeros(int count)
    {
        var values = new List<long>(count);

        CollectionsMarshal.SetCount(values, count);

        return values;
    }

    private static List<string> Empty(int count) => [.. Enumerable.Repeat("", count)];
}
