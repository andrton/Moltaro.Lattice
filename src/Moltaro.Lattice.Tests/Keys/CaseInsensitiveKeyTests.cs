using Moltaro.Lattice.TestSupport.Text;
using static Moltaro.Lattice.Tests.Keys.StringKeySchema;

namespace Moltaro.Lattice.Tests.Keys;

// A String key compared without case (Q04; brief of Epic 1, sprint S07). It follows the case table of
// Lattice's own (decision on strings, Q04, the addition of 15 November 2026; the owner's answer to
// Q46): two keys are one key when they are equal scalar value by scalar value after the simple
// uppercase mapping of Unicode 16.0.0, the same in every process and under every globalization mode.
// An entity is found under every spelling of its key and under no other text; an insert under another
// spelling of a stored key is DuplicateKey; a replace and an upsert through another spelling keep the
// spelling the entity was created with, since the primary key is immutable, and replace every other
// field; a delete through another spelling deletes. All of it alike in a UTF-8 and in a UTF-16
// database, through the class and by name.
//
// What a lookup finds is computed by the rule from the data of Unicode 16.0.0 (CaseRule of
// Moltaro.Lattice.TestSupport), never by the runtime, and not written down. The keys and the texts
// looked up are chosen so that every comparison that differs from the table gives a wrong answer for
// some of them:
// - a folding of ASCII letters only does not find Été as ÉTÉ, Жук as ЖУК, ΟΔΟΣ as οδοσ or 𐐀𐐩 as 𐐨𐐁;
// - a mapping of each UTF-16 code unit, which stops at the basic plane, does not find 𐐀𐐩 (Deseret)
//   as 𐐨𐐁 or 𐐀𐐁;
// - a comparison by the invariant culture that ignores case finds kilo under the Kelvin sign K, café
//   under cafe with a combining accent, ab under a soft hyphen between its letters, Straße under
//   STRAẞE and the mathematical bold 𝐀 under 𝐚, all of which the table holds different;
// - a mapping to lowercase does not find ΟΔΟΣ as οδος, with a final sigma, or 5µm, with the micro
//   sign, as 5ΜM, and finds kilo under the Kelvin sign;
// - StringComparison.OrdinalIgnoreCase of .NET, in every globalization mode, does not find sun as ſun,
//   with a long s, whose simple uppercase mapping is S;
// - a full case mapping finds Straße under STRASSE.
// The hash has to agree with the comparison: a hash that does not follow the table puts two spellings
// of one key in different buckets, and the other spelling is not found.
//
// Engine only: the placeholder refuses an entity type with a comparison without case until S12.
public sealed class CaseInsensitiveKeyTests
{
    // Keys that the case table holds different from each other.
    private static readonly string[] StoredKeys =
    [
        "Main", "Été", "Жук", "ΟΔΟΣ", "5µm", "𐐀𐐩", "sun", "kilo", "Straße", "café", "ab", "𝐀", "42", "",
    ];

    // Other spellings of the keys, and texts that only a wrong comparison takes for one, besides the keys
    // themselves and their spelling by the table.
    private static readonly string[] OtherTexts =
    [
        "mAIN", "éTÉ", "οδος", "οδοσ", "5ΜM", "𐐨𐐁", "𐐀𐐁", "ſun", "Kilo", "STRASSE", "STRAẞE", "STRAßE", "café", "CAFÉ", "a­b",
        "𝐚", "Mai", "Main ", "ΟΔΟ",
    ];

    // A stored key and two other spellings of it, which the table holds one key: ASCII, outside ASCII,
    // and outside the basic plane.
    private static readonly (string Stored, string Other, string Third)[] Spellings = [("Main", "mAIN", "MAIN"), ("Été", "éTÉ", "ÉTÉ"), ("𐐀𐐩", "𐐨𐐁", "𐐀𐐁")];

    [Fact]
    public void Spellings_are_one_key_by_the_case_table()
    {
        Assert.All(Spellings, spelling => Assert.True(CaseRule.Equal(spelling.Stored, spelling.Other) && CaseRule.Equal(spelling.Stored, spelling.Third)));
    }

    [Theory(Skip = NotImplemented.CaseTable)]
    [InlineData(LatticeStringEncodingEnum.Utf8)]
    [InlineData(LatticeStringEncodingEnum.Utf16)]
    public async Task Entity_is_found_under_every_spelling_of_its_key_and_under_no_other_text(LatticeStringEncodingEnum encoding)
    {
        await using var database = await OpenAsync(encoding);
        var notes = database.Set<Note>();
        var records = database.Set("Note");

        // No two keys are one key, or an insert would be refused.
        Assert.All(StoredKeys, key => Assert.Single(StoredKeys, other => CaseRule.Equal(key, other)));

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
            .Concat(StoredKeys.Select(CaseRule.ToUpper))
            .Concat(OtherTexts);

        foreach (var text in texts)
        {
            var expected = Array.FindIndex(StoredKeys, key => CaseRule.Equal(key, text));
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

        foreach (var (stored, _, _) in Spellings)
        {
            await notes.InsertAsync(new Note { Name = stored, Body = "first" });
        }

        // Through the class and by name, in turn.
        for (var index = 0; index < Spellings.Length; index++)
        {
            var other = Spellings[index].Other;

            if (index % 2 == 0)
            {
                await TestData.ExpectErrorAsync(LatticeErrorEnum.DuplicateKey, () => notes.InsertAsync(new Note { Name = other, Body = "second" }));
            }
            else
            {
                await TestData.ExpectErrorAsync(LatticeErrorEnum.DuplicateKey, () => records.InsertAsync(RecordOf(records, other, "second")));
            }
        }

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

        foreach (var spelling in Spellings)
        {
            var (stored, other, third) = spelling;

            await notes.InsertAsync(new Note { Name = stored, Body = "created" });

            // A replace through the class.
            await notes.ReplaceAsync(new Note { Name = other, Body = "replaced" });

            ExpectStored(notes, records, spelling, "replaced");

            // A replace by name with the revision read under another spelling.
            Assert.True(records.TryGet(other, out _, out var revision));
            await records.ReplaceAsync(RecordOf(records, other, "replaced again"), revision);

            ExpectStored(notes, records, spelling, "replaced again");

            // An upsert by name and one through the class.
            await records.UpsertAsync(RecordOf(records, other, "upserted"));

            ExpectStored(notes, records, spelling, "upserted");

            await notes.UpsertAsync(new Note { Name = third, Body = "upserted again" });

            ExpectStored(notes, records, spelling, "upserted again");
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

        foreach (var (stored, _, _) in Spellings)
        {
            await notes.InsertAsync(new Note { Name = stored, Body = "first" });
        }

        // Through the class, by name, and by name with the revision read under the stored spelling, in
        // turn.
        for (var index = 0; index < Spellings.Length; index++)
        {
            var (stored, other, _) = Spellings[index];
            Assert.True(notes.TryGet(stored, out _, out var revision));

            var deleted = (index % 3) switch
            {
                0 => await notes.DeleteAsync(other),
                1 => await records.DeleteAsync(other),
                _ => await records.DeleteAsync(other, revision),
            };

            Assert.True(deleted.Changed);
        }

        foreach (var (stored, other, _) in Spellings)
        {
            Assert.Null(notes.Get(stored));
            Assert.False((await notes.DeleteAsync(other)).Changed);
        }

        Assert.Empty(records.Enumerate());
    }

    private static ValueTask<ILatticeDatabase> OpenAsync(LatticeStringEncodingEnum encoding) =>
        TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine, StringKeySchema.Of(encoding, LatticeStringComparisonEnum.OrdinalIgnoreCase));

    // The entity of the key has the spelling it was created with and the body, through the class and
    // by name, under each of its spellings.
    private static void ExpectStored(ILatticeSet<Note> notes, ILatticeSet records, (string Stored, string Other, string Third) spelling, string body)
    {
        var stored = spelling.Stored;

        foreach (var text in new[] { stored, spelling.Other, spelling.Third })
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
