using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// An exact search that returns one entity allocates its result and nothing for a comparison, and making
// its key allocates nothing (docs/design/ordered-index.md, "The search by a full key": the key is bound
// once, outside the protection, and its fixed parts are written on the stack; IndexKey: making a key
// allocates nothing and boxes none of its values). Measured as the read by key is, with
// GC.GetAllocatedBytesForCurrentThread, in the test collection of the tests that measure managed
// allocation.
// - A search through a class and by name, by Find and by FindAsync, that returns one entity allocates as
//   many bytes in an index of 3,000 entries, whose descent passes inner nodes, as in an index of that
//   entity alone: the comparisons of the descent and of the run allocate nothing. A search that finds
//   nothing allocates the same in both, too. Each is measured after a first search of its form, which may
//   reserve what a comparison of a key of its length needs.
// - The keys are an Int64 of Counted (IndexSchema), and a String of 8, 64 and 100 bytes, a copy and a
//   shared key, with either comparison, in a UTF-8 and in a UTF-16 database; the texts of one index share
//   their head and end with their number, as numbered logins do, and a key compared without case is
//   given in uppercase.
// - Making a key of every scalar type, of a Nullable of one, of a string, of a value given as an object,
//   of four values and with a range allocates nothing, in a build whose code the JIT does not optimize as
//   in one whose code it does: the remarks of IndexKey name no configuration.
[Collection<ManagedAllocationCollection>]
public sealed class SearchAllocationTests
{
    private const int Entries = 3_000;

    private static readonly LatticeFindOptions Options = new();

    private static readonly string[] Forms = ["Find through a class", "Find by name", "FindAsync through a class", "FindAsync by name"];

    public static IEnumerable<TheoryDataRow<string, string, int>> TextCases =>
        from encoding in new[] { "Utf8", "Utf16" }
        from comparison in new[] { "Ordinal", "OrdinalIgnoreCase" }
        from bytes in new[] { 8, 64, 100 }
        select new TheoryDataRow<string, string, int>(encoding, comparison, bytes);

    [Fact]
    public async Task Search_by_an_Int64_key_that_returns_one_entity_allocates_as_much_in_a_deep_index_as_in_an_index_of_that_entity_alone()
    {
        await using var alone = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        await using var deep = await EngineMaster.OpenAsync(IndexSchema.Utf8);

        await alone.Set<IndexSchema.Counted>().InsertAsync(new IndexSchema.Counted { Id = Entries / 2, Number = Entries / 2 });

        foreach (var number in Numbers())
        {
            await deep.Set<IndexSchema.Counted>().InsertAsync(new IndexSchema.Counted { Id = number, Number = number });
        }

        foreach (var form in Forms)
        {
            ExpectSameInBoth(form, alone, deep, database => SearchCounted(database, form, Entries / 2), found: 1);
            ExpectSameInBoth(form, alone, deep, database => SearchCounted(database, form, Entries + 1), found: 0);
        }
    }

    [Theory]
    [MemberData(nameof(TextCases))]
    public async Task Search_by_a_String_key_that_returns_one_entity_allocates_as_much_in_a_deep_index_as_in_an_index_of_that_entity_alone(string encoding, string comparison, int bytes)
    {
        var schema = SchemaOf(encoding, comparison);

        await using var alone = await EngineMaster.OpenAsync(schema);
        await using var deep = await EngineMaster.OpenAsync(schema);

        await alone.Set<Note>().InsertAsync(new Note { Id = Entries / 2, Title = TitleOf(encoding, bytes, Entries / 2) });

        foreach (var number in Numbers())
        {
            await deep.Set<Note>().InsertAsync(new Note { Id = number, Title = TitleOf(encoding, bytes, number) });
        }

        // Under the comparison without case the key is given in another spelling than it is stored in.
        var found = TitleOf(encoding, bytes, Entries / 2);
        var key = comparison == "OrdinalIgnoreCase" ? found.ToUpperInvariant() : found;
        var absent = TitleOf(encoding, bytes, Entries + 1);

        foreach (var form in Forms)
        {
            ExpectSameInBoth(form, alone, deep, database => SearchNote(database, form, key), found: 1);
            ExpectSameInBoth(form, alone, deep, database => SearchNote(database, form, absent), found: 0);
        }
    }

    [Fact]
    public void Making_a_key_or_a_bound_of_any_scalar_value_allocates_nothing()
    {
        var text = "ada@example.org";
        object boxed = 42L;
        var guid = Guid.NewGuid();
        Func<IndexKey>[] keys =
        [
            () => IndexKey.Equal(true),
            () => IndexKey.Equal((sbyte)-1),
            () => IndexKey.Equal((byte)1),
            () => IndexKey.Equal((short)-1),
            () => IndexKey.Equal((ushort)1),
            () => IndexKey.Equal(-1),
            () => IndexKey.Equal(1u),
            () => IndexKey.Equal(-1L),
            () => IndexKey.Equal(1ul),
            () => IndexKey.Equal(1.5f),
            () => IndexKey.Equal(1.5d),
            () => IndexKey.Equal(1.5m),
            () => IndexKey.Equal(guid),
            () => IndexKey.Equal(DateTimeOffset.UnixEpoch),
            () => IndexKey.Equal(DateOnly.MaxValue),
            () => IndexKey.Equal(TimeOnly.MinValue),
            () => IndexKey.Equal(TimeSpan.MaxValue),
            () => IndexKey.Equal<int?>(36),
            () => IndexKey.Equal<long?>(null),
            () => IndexKey.Equal(text),
            () => IndexKey.Equal<string?>(null),
            () => IndexKey.Equal(boxed),
            () => IndexKey.Equal(36, text, guid, 1.5m),
            () => IndexKey.Equal(36).Range(IndexBound.Inclusive(text), IndexBound.Exclusive(text)),
            () => IndexKey.All.Range(IndexBound.Inclusive(1L), null),
        ];

        for (var index = 0; index < keys.Length; index++)
        {
            _ = keys[index]();

            var start = GC.GetAllocatedBytesForCurrentThread();
            _ = keys[index]();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            ManagedAllocationAssert.Nothing($"the key {index}", allocated);
        }
    }

    // The search allocates as many bytes in the deep index as in the index of one entry, and something.
    private static void ExpectSameInBoth(string form, EngineDatabase alone, EngineDatabase deep, Func<EngineDatabase, int> search, int found)
    {
        var inAlone = Measure(alone, search, found);
        var inDeep = Measure(deep, search, found);

        Assert.True(found == 0 || inAlone > 0, $"{form} allocated nothing, not even its result.");
        Assert.True(inAlone == inDeep, $"{form} allocated {inAlone} bytes in an index of one entry and {inDeep} in one of {Entries}, for a search that found {found}.");
    }

    // What one search allocates, after a first one; it returns the number of entities given.
    private static long Measure(EngineDatabase database, Func<EngineDatabase, int> search, int found)
    {
        Assert.Equal(found, search(database));

        var start = GC.GetAllocatedBytesForCurrentThread();
        var count = search(database);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal(found, count);

        return allocated;
    }

    // One search of ByNumber of Counted, in the form named, to its end; how many entities it returned.
    private static int SearchCounted(EngineDatabase database, string form, long number) => form switch
    {
        "Find through a class" => Count(database.Set<IndexSchema.Counted>().Index("ByNumber").Find(IndexKey.Equal(number))),
        "Find by name" => Count(database.Set("Counted").Index("ByNumber").Find(IndexKey.Equal(number))),
        "FindAsync through a class" => OwnThread.Wait(database.Set<IndexSchema.Counted>().Index("ByNumber").FindAsync(IndexKey.Equal(number), Options)).Items.Count,
        _ => OwnThread.Wait(database.Set("Counted").Index("ByNumber").FindAsync(IndexKey.Equal(number), Options)).Items.Count,
    };

    private static int SearchNote(EngineDatabase database, string form, string title) => form switch
    {
        "Find through a class" => Count(database.Set<Note>().Index("ByTitle").Find(IndexKey.Equal(title))),
        "Find by name" => Count(database.Set("Note").Index("ByTitle").Find(IndexKey.Equal(title))),
        "FindAsync through a class" => OwnThread.Wait(database.Set<Note>().Index("ByTitle").FindAsync(IndexKey.Equal(title), Options)).Items.Count,
        _ => OwnThread.Wait(database.Set("Note").Index("ByTitle").FindAsync(IndexKey.Equal(title), Options)).Items.Count,
    };

    // Enumerates the matches as an application does, to the end, without keeping them.
    private static int Count<TEntity>(IEnumerable<TEntity> matches)
    {
        var count = 0;

        foreach (var _ in matches)
        {
            count++;
        }

        return count;
    }

    // The numbers 0 to 2,999 in an order that is neither ascending nor descending.
    private static IEnumerable<int> Numbers() => Enumerable.Range(0, Entries).Select(step => (int)((step * 1_597L) % Entries));

    // A text of that many bytes in the encoding of the database, of lowercase letters of ASCII, that ends
    // with the number in three letters.
    private static string TitleOf(string encoding, int bytes, int number)
    {
        var characters = encoding == "Utf8" ? bytes : bytes / 2;
        var letters = new[] { (char)('a' + (number / 676 % 26)), (char)('a' + (number / 26 % 26)), (char)('a' + (number % 26)) };

        return new string('k', characters - letters.Length) + new string(letters);
    }

    private static LatticeSchema SchemaOf(string encoding, string comparison) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note", "ClrType": "{{typeof(Note).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Title", "Type": "String", "Comparison": "{{comparison}}" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByTitle", "Fields": [2] } ]
            }
          ]
        }
        """);

    private sealed class Note
    {
        public long Id { get; set; }

        public string Title { get; set; } = "";
    }
}
