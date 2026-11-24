namespace Moltaro.Lattice.Tests.NestedValues.Lists;

// A List keeps the supplied order and its duplicates across the boundary of a chunk, in each of its two
// forms (docs/design/milestone-1-engine-contract.md, "Collections and strings" and "Native layout and
// allocation": never reorder elements, and a chunk boundary is not a boundary of the collection). The figures are computed from the layout, not from the code: a
// chunk holds 65,504 bytes (ValueBlocks.ChunkPayload), and a value of more than 4,096 bytes
// (ValueBlocks.SingleBlockLimit) lies in chunks.
//
// Packed, a List whose elements hold no descriptor lies as the bytes of a Binary value, its elements at the
// stride of their record one after the other, and an element may continue in the next chunk:
// - Scores, of Int64, a stride of 8 bytes: 65,504 / 8 = 8,188 elements fill one chunk exactly, the last
//   element that fits; the 8,189th is the first that does not, alone in a second chunk;
// - Marks, of nullable Int32, a record of a bitmap of one byte and the four bytes of the value, a stride of
//   5: 65,504 = 5 × 13,100 + 4, so the element at index 13,100 lies at bytes 65,500 to 65,504, four of them
//   in the first chunk and one in the second: it continues across the boundary.
// As records, a List whose elements hold descriptors lies whole records in a block, each with room inline for
// a value of 64 bytes (ValueBlocks.InlineLimit) for each of its descriptors:
// - Tags, of String, a record of one descriptor of 16 bytes: 65,504 / (16 + 64) = 818 records, rounded
//   down, in one chunk; with every element a text of 64 bytes the 818th is the last that fits, 818 × 80 =
//   65,440 bytes, and the 819th the first that does not, in a second chunk;
// - Places, of owned Addresses: a bitmap of one byte for the presence bit of Geo and the two bits of the
//   fields beneath it, Zip of 4 bytes, Lat and Lon of 8 each, and the descriptors of City and Lines, 16
//   each, a stride of 1 + 20 + 32 = 53 bytes with two descriptors: 65,504 / (53 + 2 × 64) = 361 records,
//   rounded down; the 361st is the last that fits and the 362nd the first that does not.
// At each boundary the element before it and the element after it are equal, a duplicate across the
// boundary, and every other element is distinct and in an order that is not sorted, so that a List read
// back in another order, without the duplicate or with one element twice, differs from the one written.
// Each List is written by name and read back by name and through the class.
public sealed class ListChunkBoundaryTests
{
    private const int Int64InOneChunk = 65_504 / 8;

    private const int Int32RecordStride = 1 + 4;

    // The index of the nullable Int32 element whose bytes the boundary of the first chunk divides.
    private const int AcrossTheBoundary = 65_504 / Int32RecordStride;

    private const int TextRecordsInOneChunk = 65_504 / (16 + 64);

    private const int AddressRecordsInOneChunk = 65_504 / (1 + 4 + 8 + 8 + 16 + 16 + (2 * 64));

    [Theory(Skip = SkipReason.NestedValues)]
    [InlineData(Int64InOneChunk)]
    [InlineData(Int64InOneChunk + 1)]
    public async Task Packed_List_keeps_its_order_and_its_duplicates_at_the_boundary_of_a_chunk(int count)
    {
        Assert.Equal(8_188, Int64InOneChunk);

        var person = NestedSchema.Full($"scores {count}");
        person.Scores = [.. Descending(count, boundary: Int64InOneChunk).Select(value => (long)value * 1_000_003)];

        await ExpectReadBackAsync(person);
    }

    [Theory(Skip = SkipReason.NestedValues)]
    [InlineData(AcrossTheBoundary + 1)]
    [InlineData(AcrossTheBoundary + 2)]
    public async Task Packed_element_that_continues_across_the_boundary_of_a_chunk_is_read_back_whole(int count)
    {
        Assert.Equal(13_100, AcrossTheBoundary);

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

        await ExpectReadBackAsync(person);
    }

    [Theory(Skip = SkipReason.NestedValues)]
    [InlineData(TextRecordsInOneChunk)]
    [InlineData(TextRecordsInOneChunk + 1)]
    public async Task List_of_records_keeps_its_order_and_its_duplicates_at_the_boundary_of_a_chunk(int count)
    {
        Assert.Equal(818, TextRecordsInOneChunk);

        // Texts of exactly 64 bytes in UTF-8, so that each record uses all the room it has inline.
        var person = NestedSchema.Full($"tags {count}");
        person.Tags = [.. Descending(count, boundary: TextRecordsInOneChunk).Select(value => value.ToString("D64", System.Globalization.CultureInfo.InvariantCulture))];

        Assert.All(person.Tags, tag => Assert.Equal(64, tag.Length));

        await ExpectReadBackAsync(person);
    }

    [Theory(Skip = SkipReason.NestedValues)]
    [InlineData(AddressRecordsInOneChunk)]
    [InlineData(AddressRecordsInOneChunk + 1)]
    public async Task List_of_owned_values_keeps_its_order_and_its_duplicates_at_the_boundary_of_a_chunk(int count)
    {
        Assert.Equal(361, AddressRecordsInOneChunk);

        // Each City and the one line of each Address 64 bytes, Geo present in every third.
        var person = NestedSchema.Full($"places {count}");
        person.Places =
        [
            .. Descending(count, boundary: AddressRecordsInOneChunk).Select(value => new Address
            {
                City = value.ToString("D64", System.Globalization.CultureInfo.InvariantCulture),
                Zip = value,
                Lines = [new string((char)('a' + (value % 26)), 64)],
                Geo = value % 3 == 0 ? new Point { Lat = value, Lon = -value } : null,
            }),
        ];

        await ExpectReadBackAsync(person);
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

    private static async Task ExpectReadBackAsync(Person person)
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var expected = NestedSchema.Describe(database.Schema, person);

        await database.Set("Person").InsertAsync(NestedSchema.RecordOf(database.Schema, person));

        Assert.Equal(expected, NestedSchema.Describe(database.Set("Person").Get(person.Code)));
        Assert.Equal(expected, NestedSchema.Describe(database.Schema, database.Set<Person>().Get(person.Code)));
    }
}
