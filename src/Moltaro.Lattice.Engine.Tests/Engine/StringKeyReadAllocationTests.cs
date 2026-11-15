using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// Asserted (brief of Epic 1, sprint S07, Measured): a read by a String key allocates the returned copy
// and nothing else, with either comparison, in a UTF-8 and in a UTF-16 database, after a first read
// with a key of that length, which may reserve the workspace of the comparison. So comparing a key
// allocates nothing that outlives the read: neither a string of the stored key nor one of the key
// given, nor anything per comparison. The returned copy is as ReadAllocationTests has it: the object
// with its two strings, or the record with the array of its values and the two strings.
//
// The key repeats A, é, € and 𐐨, characters of one to four bytes in UTF-8, of which the first, the
// second and the last have a case; it lies inline, in a block of its own and in chunks. Under
// OrdinalIgnoreCase the measured reads are by the key and by its upper case, which differs from it
// outside ASCII and outside the basic plane, so the workspace the first read reserved serves another
// spelling of the same length; a read of an absent key of that length allocates nothing. Measured as
// ReadAllocationTests measures, in the test collection of the tests that measure managed allocation.
[Collection<ManagedAllocationCollection>]
public sealed class StringKeyReadAllocationTests
{
    private const string Body = "body";

    public static IEnumerable<TheoryDataRow<LatticeStringEncodingEnum, LatticeStringComparisonEnum, int>> Cases =>
        from encoding in new[] { LatticeStringEncodingEnum.Utf8, LatticeStringEncodingEnum.Utf16 }
        from comparison in new[] { LatticeStringComparisonEnum.Ordinal, LatticeStringComparisonEnum.OrdinalIgnoreCase }
        from length in new[] { 10, 1_000, 100_000 }
        select new TheoryDataRow<LatticeStringEncodingEnum, LatticeStringComparisonEnum, int>(encoding, comparison, length);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Read_by_a_String_key_through_a_class_allocates_the_returned_object_and_its_strings_and_nothing_else(
        LatticeStringEncodingEnum encoding,
        LatticeStringComparisonEnum comparison,
        int length)
    {
        await using var database = await EngineMaster.OpenAsync(SchemaOf(encoding, comparison));
        var notes = database.Set<Note>();
        var (key, spellings, absent) = KeysOf(length, comparison);
        await notes.InsertAsync(new Note { Name = key, Body = Body });
        var expected = Measure(() => new Note()) + Measure(() => new string(' ', length)) + Measure(() => new string(' ', Body.Length));

        // The first reads with a key of this length.
        _ = notes.Get(key);
        _ = notes.TryGet(key, out _, out _);

        foreach (var spelling in spellings)
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            var read = notes.Get(spelling);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Assert.Equal(key, read!.Name);
            Assert.Equal(expected, allocated);

            start = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(notes.TryGet(spelling, out var withRevision, out _));
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Assert.Equal(Body, withRevision.Body);
            Assert.Equal(expected, allocated);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var missing = notes.Get(absent);
        var allocatedForMissing = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(missing);
        Assert.Equal(0, allocatedForMissing);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Read_by_a_String_key_by_name_allocates_the_returned_record_and_its_strings_and_nothing_else(
        LatticeStringEncodingEnum encoding,
        LatticeStringComparisonEnum comparison,
        int length)
    {
        await using var database = await EngineMaster.OpenAsync(SchemaOf(encoding, comparison));
        var notes = database.Set("Note");
        var (key, spellings, absent) = KeysOf(length, comparison);
        await notes.InsertAsync(new LatticeRecord(notes.EntityType) { ["Name"] = key, ["Body"] = Body });
        var expected = Measure(() => new LatticeRecord(notes.EntityType)) + Measure(() => new string(' ', length)) + Measure(() => new string(' ', Body.Length));

        // The first reads with a key of this length.
        _ = notes.Get(key);
        _ = notes.TryGet(key, out _, out _);

        foreach (var spelling in spellings)
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            var read = notes.Get(spelling);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Assert.Equal(key, read!["Name"]);
            Assert.Equal(expected, allocated);

            start = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(notes.TryGet(spelling, out var withRevision, out _));
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Assert.Equal(Body, withRevision["Body"]);
            Assert.Equal(expected, allocated);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var missing = notes.Get(absent);
        var allocatedForMissing = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(missing);
        Assert.Equal(0, allocatedForMissing);
    }

    // The key of the length, the spellings it is read by, and an absent key of the same length.
    private static (string Key, string[] Spellings, string Absent) KeysOf(int length, LatticeStringComparisonEnum comparison)
    {
        const string Pattern = "Aé€𐐨";

        var key = string.Concat(Enumerable.Repeat(Pattern, length / Pattern.Length));
        string[] spellings = comparison == LatticeStringComparisonEnum.OrdinalIgnoreCase ? [key, key.ToUpperInvariant()] : [key];

        return (key, spellings, 'Z' + key[1..]);
    }

    // What the call allocates, measured after it ran once.
    private static long Measure(Func<object> call)
    {
        _ = call();

        var start = GC.GetAllocatedBytesForCurrentThread();
        _ = call();

        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static LatticeSchema SchemaOf(LatticeStringEncodingEnum encoding, LatticeStringComparisonEnum comparison) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note", "ClrType": "{{typeof(Note).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Name", "Type": "String", "Comparison": "{{comparison}}" },
                { "Id": 2, "Name": "Body", "Type": "String" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private sealed class Note
    {
        public string Name { get; set; } = "";

        public string Body { get; set; } = "";
    }
}
