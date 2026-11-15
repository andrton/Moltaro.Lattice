using static Moltaro.Lattice.Tests.Keys.StringKeySchema;

namespace Moltaro.Lattice.Tests.Keys;

// A String key of 100,000 characters is found with either comparison, in a UTF-8 and in a UTF-16
// database (brief of Epic 1, sprint S07): read, replaced and deleted through its class and by name.
// The key repeats a, é, € and 𐐨, characters of one, two, three and four bytes in UTF-8, so that it
// lies in chunks of the arena whose boundaries fall inside a character, and in UTF-16 between the two
// halves of a surrogate pair. Under Ordinal another spelling is another key; under OrdinalIgnoreCase
// the key is found as its upper case as well, where .NET holds the two equal in the process the test
// runs in: it does with ICU, and under NLS, which holds the Deseret letters different, the upper case
// is another key. A key that differs only in its last character, after every chunk, is another key
// under both comparisons.
//
// The placeholder stores a String key compared with Ordinal in a UTF-8 database and runs that case
// too; the other cases run on the engine only, which the placeholder refuses until S12.
public sealed class LongStringKeyTests
{
    private const int Length = 100_000;

    public static IEnumerable<TheoryDataRow<DatabaseImplementationEnum, LatticeStringEncodingEnum, LatticeStringComparisonEnum>> Cases =>
    [
        new(DatabaseImplementationEnum.Placeholder, LatticeStringEncodingEnum.Utf8, LatticeStringComparisonEnum.Ordinal),
        new(DatabaseImplementationEnum.Engine, LatticeStringEncodingEnum.Utf8, LatticeStringComparisonEnum.Ordinal),
        new(DatabaseImplementationEnum.Engine, LatticeStringEncodingEnum.Utf16, LatticeStringComparisonEnum.Ordinal),
        new(DatabaseImplementationEnum.Engine, LatticeStringEncodingEnum.Utf8, LatticeStringComparisonEnum.OrdinalIgnoreCase),
        new(DatabaseImplementationEnum.Engine, LatticeStringEncodingEnum.Utf16, LatticeStringComparisonEnum.OrdinalIgnoreCase),
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task String_key_of_100000_characters_is_found_with_either_comparison(
        DatabaseImplementationEnum implementation,
        LatticeStringEncodingEnum encoding,
        LatticeStringComparisonEnum comparison)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, StringKeySchema.Of(encoding, comparison));
        var notes = database.Set<Note>();
        var records = database.Set("Note");
        var key = KeyEndingWith('z');
        var upper = key.ToUpperInvariant();
        var ignoresCase = comparison == LatticeStringComparisonEnum.OrdinalIgnoreCase;

        await notes.InsertAsync(new Note { Name = key, Body = "first" });
        await records.InsertAsync(RecordOf(records, KeyEndingWith('y'), "other"));

        Assert.Equal(Length, key.Length);
        Assert.Equal(key, notes.Get(key)!.Name);
        Assert.Equal("first", records.Get(key)!["Body"]);
        Assert.Equal("other", notes.Get(KeyEndingWith('y'))!.Body);
        Assert.Null(notes.Get(KeyEndingWith('x')));

        // Its upper case is the key where the comparison of .NET holds the two equal in this process,
        // never under Ordinal, and then it is found with the spelling it was created with.
        var upperIsTheKey = string.Equals(key, upper, ignoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        if (upperIsTheKey)
        {
            Assert.Equal(key, records.Get(upper)!["Name"]);
        }
        else
        {
            Assert.Null(records.Get(upper));
        }

        // Replaced through the class and by name, then deleted, under the spelling the comparison
        // finds it by.
        var spelling = upperIsTheKey ? upper : key;

        await notes.ReplaceAsync(new Note { Name = spelling, Body = "replaced" });
        Assert.Equal("replaced", notes.Get(key)!.Body);

        await records.UpsertAsync(RecordOf(records, spelling, "upserted"));
        Assert.Equal("upserted", records.Get(key)!["Body"]);
        Assert.Equal(key, notes.Get(key)!.Name);

        Assert.True((await records.DeleteAsync(spelling)).Changed);
        Assert.Null(notes.Get(key));
        Assert.Equal("other", Assert.Single(notes.Enumerate()).Body);
    }

    // A key of Length code units: 19,999 times a, é, € and 𐐨, five code units, then four letters and
    // the last character given.
    private static string KeyEndingWith(char last)
    {
        const string Pattern = "aé€𐐨";

        return string.Concat(Enumerable.Repeat(Pattern, (Length - 5) / Pattern.Length)) + "abcd" + last;
    }
}
