using static Moltaro.Lattice.Tests.Keys.StringKeySchema;

namespace Moltaro.Lattice.Tests.Keys;

// A String key compared without case (Q04; brief of Epic 1, sprint S07). It follows
// StringComparison.OrdinalIgnoreCase of .NET, the semantic reference Q04 names: two keys that it holds
// equal are one key. An entity is found under every spelling of its key and under no other text; an
// insert under another spelling of a stored key is DuplicateKey; a replace and an upsert through
// another spelling keep the spelling the entity was created with, since the primary key is immutable,
// and replace every other field; a delete through another spelling deletes. All of it alike in a UTF-8
// and in a UTF-16 database, through the class and by name.
//
// What a lookup finds is computed with string.Equals(…, StringComparison.OrdinalIgnoreCase) and not
// written down. The keys and the texts looked up are chosen so that every case folding that differs
// from it gives a wrong answer for some of them, as measured on .NET 10:
// - a folding of ASCII letters only does not find Été as ÉTÉ, Жук as ЖУК, ΟΔΟΣ as οδοσ or 𐐀𐐩 as 𐐨𐐁;
// - a folding of each UTF-16 code unit, which stops at the basic plane, does not find 𐐀𐐩 (Deseret)
//   as 𐐨𐐁 or 𐐀𐐁;
// - a comparison by the invariant culture that ignores case finds kilo under the Kelvin sign K, café
//   under cafe with a combining accent, ab under a soft hyphen between its letters, Straße under
//   STRAẞE and the mathematical bold 𝐀 under 𝐚, all of which OrdinalIgnoreCase holds different;
// - a folding by ToLowerInvariant does not find ΟΔΟΣ as οδος, with a final sigma, or 5µm, with the
//   micro sign, as 5ΜM, and finds kilo under the Kelvin sign;
// - a folding by ToUpperInvariant finds sun under ſun, with a long s;
// - a full case folding finds Straße under STRASSE.
// The hash has to agree with the comparison: a hash of the code units under an equality that ignores
// case finds no other spelling.
//
// Engine only: the placeholder refuses an entity type with a comparison without case until S12.
public sealed class CaseInsensitiveKeyTests
{
    // Keys that OrdinalIgnoreCase holds different from each other.
    private static readonly string[] StoredKeys =
    [
        "Main", "Été", "Жук", "ΟΔΟΣ", "5µm", "𐐀𐐩", "sun", "kilo", "Straße", "café", "ab", "𝐀", "42", "",
    ];

    // Other spellings of the keys, and texts that only a wrong folding takes for one, besides the keys
    // themselves and their upper and lower case by the invariant culture.
    private static readonly string[] OtherTexts =
    [
        "mAIN", "éTÉ", "οδος", "οδοσ", "5ΜM", "𐐨𐐁", "𐐀𐐁", "ſun", "Kilo", "STRASSE", "STRAẞE", "STRAßE", "café", "CAFÉ", "a­b",
        "𝐚", "Mai", "Main ", "ΟΔΟ",
    ];

    // A stored key and another spelling of it: ASCII, outside ASCII, and outside the basic plane.
    private static readonly (string Stored, string Other)[] Spellings = [("Main", "mAIN"), ("Été", "éTÉ"), ("𐐀𐐩", "𐐨𐐁")];

    [Theory]
    [InlineData(LatticeStringEncodingEnum.Utf8)]
    [InlineData(LatticeStringEncodingEnum.Utf16)]
    public async Task Entity_is_found_under_every_spelling_of_its_key_and_under_no_other_text(LatticeStringEncodingEnum encoding)
    {
        await using var database = await OpenAsync(encoding);
        var notes = database.Set<Note>();
        var records = database.Set("Note");

        // No two keys are one key, or an insert would be refused.
        Assert.All(StoredKeys, key => Assert.Single(StoredKeys, other => string.Equals(key, other, StringComparison.OrdinalIgnoreCase)));

        for (var index = 0; index < StoredKeys.Length; index++)
        {
            var body = $"body {index}";

            if (index % 2 == 0)
            {
                await notes.InsertAsync(new Note { Name = StoredKeys[index], Body = body });
            }
            else
            {
                await records.InsertAsync(RecordOf(records, StoredKeys[index], body));
            }
        }

        var texts = StoredKeys
            .Concat(StoredKeys.Select(key => key.ToUpperInvariant()))
            .Concat(StoredKeys.Select(key => key.ToLowerInvariant()))
            .Concat(OtherTexts);

        foreach (var text in texts)
        {
            var expected = Array.FindIndex(StoredKeys, key => string.Equals(key, text, StringComparison.OrdinalIgnoreCase));
            var note = notes.Get(text);
            var record = records.Get(text);

            if (expected < 0)
            {
                Assert.Null(note);
                Assert.Null(record);
                continue;
            }

            // Found with the spelling it was created with.
            Assert.NotNull(note);
            Assert.Equal(StoredKeys[expected], note.Name);
            Assert.Equal($"body {expected}", note.Body);
            Assert.NotNull(record);
            Assert.Equal(StoredKeys[expected], record["Name"]);
        }
    }

    [Theory]
    [InlineData(LatticeStringEncodingEnum.Utf8)]
    [InlineData(LatticeStringEncodingEnum.Utf16)]
    public async Task Insert_under_another_spelling_of_a_stored_key_is_DuplicateKey(LatticeStringEncodingEnum encoding)
    {
        await using var database = await OpenAsync(encoding);
        var notes = database.Set<Note>();
        var records = database.Set("Note");

        foreach (var (stored, _) in Spellings)
        {
            await notes.InsertAsync(new Note { Name = stored, Body = "first" });
        }

        await TestData.ExpectErrorAsync(LatticeErrorEnum.DuplicateKey, () => notes.InsertAsync(new Note { Name = Spellings[0].Other, Body = "second" }));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.DuplicateKey, () => records.InsertAsync(RecordOf(records, Spellings[1].Other, "second")));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.DuplicateKey, () => notes.InsertAsync(new Note { Name = Spellings[2].Other, Body = "second" }));

        // The stored entities are as they were.
        Assert.Equal(Spellings.Select(spelling => spelling.Stored).Order(StringComparer.Ordinal), notes.Enumerate().Select(note => note.Name).Order(StringComparer.Ordinal));
        Assert.All(notes.Enumerate(), note => Assert.Equal("first", note.Body));
    }

    [Theory]
    [InlineData(LatticeStringEncodingEnum.Utf8)]
    [InlineData(LatticeStringEncodingEnum.Utf16)]
    public async Task Replace_and_upsert_through_another_spelling_keep_the_stored_spelling_and_replace_the_other_fields(LatticeStringEncodingEnum encoding)
    {
        await using var database = await OpenAsync(encoding);
        var notes = database.Set<Note>();
        var records = database.Set("Note");

        foreach (var (stored, other) in Spellings)
        {
            await notes.InsertAsync(new Note { Name = stored, Body = "created" });

            // A replace through the class.
            await notes.ReplaceAsync(new Note { Name = other, Body = "replaced" });

            ExpectStored(notes, records, stored, "replaced");

            // A replace by name with the revision read under another spelling.
            Assert.True(records.TryGet(other, out _, out var revision));
            await records.ReplaceAsync(RecordOf(records, other, "replaced again"), revision);

            ExpectStored(notes, records, stored, "replaced again");

            // An upsert by name and one through the class.
            await records.UpsertAsync(RecordOf(records, other, "upserted"));

            ExpectStored(notes, records, stored, "upserted");

            await notes.UpsertAsync(new Note { Name = other.ToUpperInvariant(), Body = "upserted again" });

            ExpectStored(notes, records, stored, "upserted again");
        }

        // One entity for each key, with the spelling it was created with.
        Assert.Equal(Spellings.Select(spelling => spelling.Stored).Order(StringComparer.Ordinal), records.Enumerate().Select(record => (string)record["Name"]!).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(LatticeStringEncodingEnum.Utf8)]
    [InlineData(LatticeStringEncodingEnum.Utf16)]
    public async Task Delete_through_another_spelling_deletes(LatticeStringEncodingEnum encoding)
    {
        await using var database = await OpenAsync(encoding);
        var notes = database.Set<Note>();
        var records = database.Set("Note");

        foreach (var (stored, _) in Spellings)
        {
            await notes.InsertAsync(new Note { Name = stored, Body = "first" });
        }

        // Through the class, by name, and by name with the revision read under the stored spelling.
        Assert.True(notes.TryGet(Spellings[2].Stored, out _, out var revision));
        Assert.True((await notes.DeleteAsync(Spellings[0].Other)).Changed);
        Assert.True((await records.DeleteAsync(Spellings[1].Other)).Changed);
        Assert.True((await records.DeleteAsync(Spellings[2].Other, revision)).Changed);

        foreach (var (stored, other) in Spellings)
        {
            Assert.Null(notes.Get(stored));
            Assert.False((await notes.DeleteAsync(other)).Changed);
        }

        Assert.Empty(records.Enumerate());
    }

    private static ValueTask<ILatticeDatabase> OpenAsync(LatticeStringEncodingEnum encoding) =>
        TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine, StringKeySchema.Of(encoding, LatticeStringComparisonEnum.OrdinalIgnoreCase));

    // The entity of the key has the spelling it was created with and the body, through the class and
    // by name, under that spelling and under its upper case.
    private static void ExpectStored(ILatticeSet<Note> notes, ILatticeSet records, string stored, string body)
    {
        foreach (var text in new[] { stored, stored.ToUpperInvariant() })
        {
            var note = notes.Get(text);
            var record = records.Get(text);

            Assert.NotNull(note);
            Assert.Equal(stored, note.Name);
            Assert.Equal(body, note.Body);
            Assert.NotNull(record);
            Assert.Equal(stored, record["Name"]);
            Assert.Equal(body, record["Body"]);
        }
    }
}
