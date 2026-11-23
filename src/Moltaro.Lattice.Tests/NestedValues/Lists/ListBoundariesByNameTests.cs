using System.Globalization;

namespace Moltaro.Lattice.Tests.NestedValues.Lists;

// A List keeps the supplied order, its nulls and its duplicates at every boundary of where it lies, in each
// of its two forms, written by name or through the class and read by name
// (docs/design/milestone-1-engine-contract.md, "Collections and strings" and "Native layout and allocation":
// never reorder elements, and a chunk boundary is not a boundary of the collection). The figures come from
// the layout: a chunk holds 65,504 bytes, a value of at most 64 bytes lies inline and one of at most 4,096
// bytes in one block.
// - The boundary of a chunk, packed: Scores, of Int64, fill one chunk with 8,188 elements; Marks, of nullable
//   Int32, a record of 5 bytes, have the element at index 13,100 divided by the boundary.
// - The boundary of a chunk, as records: Tags, of String, 818 records of 16 + 64 bytes in one chunk; Places,
//   of owned Addresses, 361 records of 53 + 2 × 64 bytes.
// - Inline and one block, packed: 8 and 9 Int64 lie inline and not, 512 and 513 in one block and not; 12 and
//   13 nullable Int32, 819 and 820, the same boundaries for a record of 5 bytes.
// At a chunk boundary the element before it and the element after it are equal, and every other element is
// distinct and in an order that is not sorted, so that a List read back in another order, without the
// duplicate or with one element twice, differs from the one written.
public sealed class ListBoundariesByNameTests
{
    private const int Int64InOneChunk = 65_504 / 8;

    // The index of the nullable Int32 element whose bytes the boundary of the first chunk divides.
    private const int AcrossTheBoundary = 65_504 / (1 + 4);

    private const int TextRecordsInOneChunk = 65_504 / (16 + 64);

    private const int AddressRecordsInOneChunk = 65_504 / (1 + 4 + 8 + 8 + 16 + 16 + (2 * 64));

    [Theory]
    [InlineData(Int64InOneChunk)]
    [InlineData(Int64InOneChunk + 1)]
    public async Task Packed_List_keeps_its_order_and_its_duplicates_at_the_boundary_of_a_chunk_read_by_name(int count)
    {
        var person = NestedSchema.Full($"scores {count}");
        person.Scores = [.. Descending(count, boundary: Int64InOneChunk).Select(value => (long)value * 1_000_003)];

        await ExpectReadBackByNameAsync(person);
    }

    [Theory]
    [InlineData(AcrossTheBoundary + 1)]
    [InlineData(AcrossTheBoundary + 2)]
    public async Task Packed_element_that_continues_across_the_boundary_of_a_chunk_is_read_back_whole_by_name(int count)
    {
        // The element across the boundary is int.MinValue, every byte of whose value matters, after a null
        // and before the duplicate of itself, with nulls spread among the rest.
        var person = NestedSchema.Full($"marks {count}");
        var marks = Descending(count, boundary: AcrossTheBoundary + 1).Select(value => value % 7 == 0 ? (int?)null : value).ToList();

        marks[AcrossTheBoundary - 1] = null;
        marks[AcrossTheBoundary] = int.MinValue;

        if (count > AcrossTheBoundary + 1)
        {
            marks[AcrossTheBoundary + 1] = int.MinValue;
        }

        person.Marks = marks;

        await ExpectReadBackByNameAsync(person);
    }

    [Theory]
    [InlineData(TextRecordsInOneChunk)]
    [InlineData(TextRecordsInOneChunk + 1)]
    public async Task List_of_records_keeps_its_order_and_its_duplicates_at_the_boundary_of_a_chunk_read_by_name(int count)
    {
        // Texts of exactly 64 bytes, so that each record uses all the room it has inline.
        var person = NestedSchema.Full($"tags {count}");
        person.Tags = [.. Descending(count, boundary: TextRecordsInOneChunk).Select(value => value.ToString("D64", CultureInfo.InvariantCulture))];

        await ExpectReadBackByNameAsync(person);
    }

    [Theory]
    [InlineData(AddressRecordsInOneChunk)]
    [InlineData(AddressRecordsInOneChunk + 1)]
    public async Task List_of_owned_values_keeps_its_order_and_its_duplicates_at_the_boundary_of_a_chunk_read_by_name(int count)
    {
        // Each City and the one line of each Address 64 bytes, Geo present in every third.
        var person = NestedSchema.Full($"places {count}");
        person.Places =
        [
            .. Descending(count, boundary: AddressRecordsInOneChunk).Select(value => new Address
            {
                City = value.ToString("D64", CultureInfo.InvariantCulture),
                Zip = value,
                Lines = [new string((char)('a' + (value % 26)), 64)],
                Geo = value % 3 == 0 ? new Point { Lat = value, Lon = -value } : null,
            }),
        ];

        await ExpectReadBackByNameAsync(person);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(819)]
    [InlineData(820)]
    public async Task Packed_Lists_on_either_side_of_lying_inline_and_of_lying_in_one_block_are_read_back_by_name(int count)
    {
        var person = NestedSchema.Full($"sizes {count}");
        person.Scores = [.. Enumerable.Range(0, count).Select(index => (long)(count - index) * 7_919)];
        person.Marks = [.. Enumerable.Range(0, count).Select(index => index % 3 == 0 ? (int?)null : -index)];
        person.Home!.Lines = [.. Enumerable.Range(0, count % 50).Select(index => $"{index}")];
        person.Grid = [[.. Enumerable.Range(0, count)], [.. Enumerable.Range(0, count / 2)]];

        await ExpectReadBackByNameAsync(person);
    }

    // count distinct numbers in descending order, except that the number at the index boundary - 1, the last
    // before the boundary, is repeated at the index boundary, when the List reaches it.
    private static List<int> Descending(int count, int boundary)
    {
        var values = Enumerable.Range(0, count).Select(index => count - index).ToList();

        if (count > boundary)
        {
            values[boundary] = values[boundary - 1];
        }

        return values;
    }

    // Writes the person by name and, under another code, through the class, and reads both back by name.
    private static async Task ExpectReadBackByNameAsync(Person person)
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byName = database.Set("Person");

        await byName.InsertAsync(NestedSchema.RecordOf(database.Schema, person));

        Assert.Equal(NestedSchema.Describe(database.Schema, person), NestedSchema.Describe(byName.Get(person.Code)));

        person.Code += " through the class";

        await database.Set<Person>().InsertAsync(person);

        Assert.Equal(NestedSchema.Describe(database.Schema, person), NestedSchema.Describe(byName.Get(person.Code)));
    }
}
