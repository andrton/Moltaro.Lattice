using System.Text;
using Moltaro.Lattice.TestSupport.Text;

namespace Moltaro.Lattice.Tests.Indexes.Search;

// A String part of an index is found by its text, by the comparison its field declares, in a UTF-8 and in
// a UTF-16 database, with Plain and with Dictionary storage (decision on strings, Q04 and Q46: Ordinal, or
// without case by the case table of the core; docs/decisions/index-structures.md, "Search-key ownership":
// an entry copies a key of 64 bytes or less, whatever the storage of its field, and shares a longer one
// with the root). The entity type Note has the String field Title, indexed by ByTitle, in each of the
// eight forms; a test runs in each.
// - Every length is found: the empty text, keys of a few bytes, keys at the boundary of the copy in both
//   encodings — 64 bytes and the next length a text of that encoding has, in ASCII, in two-byte and in
//   four-byte characters —, 100 bytes, and a text of more than 4 KiB, whose entry lies in chunks; and long
//   keys that share their first 64 bytes and more, which differ only at their end. Each title is written
//   twice, and the search returns both entities and no other.
// - A field compared without case finds the key under any spelling the case table holds equal, the
//   uppercase of a character outside ASCII and outside the basic plane included; an Ordinal field only
//   under its own.
// - A replace that keeps an equal key under another spelling, on a field compared without case, is found
//   under the key and returns the new spelling, short and long.
// - A replace that changes the key, from short to long, from long to short and between two long keys of
//   one head, is found under the new key and not under the old; a delete takes it away and an insert
//   brings it back.
public sealed class TextKeySearchTests
{
    public static IEnumerable<TheoryDataRow<string, string, string>> Forms =>
        from encoding in new[] { "Utf8", "Utf16" }
        from comparison in new[] { "Ordinal", "OrdinalIgnoreCase" }
        from storage in new[] { "Plain", "Dictionary" }
        select new TheoryDataRow<string, string, string>(encoding, comparison, storage);

    public static IEnumerable<TheoryDataRow<string, string>> FormsWithoutCase =>
        from encoding in new[] { "Utf8", "Utf16" }
        from storage in new[] { "Plain", "Dictionary" }
        select new TheoryDataRow<string, string>(encoding, storage);

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Text_key_of_every_length_is_found_by_its_full_key_through_a_class_and_by_name(string encoding, string comparison, string storage)
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaOf(encoding, comparison, storage));
        var notes = database.Set<Note>();
        var records = database.Set("Note");
        var titles = TitlesOf(encoding);

        for (var index = 0; index < titles.Length; index++)
        {
            await notes.InsertAsync(new Note { Id = 2 * index, Title = titles[index], Body = "by class" });
            await records.InsertAsync(new LatticeRecord(records.EntityType) { ["Id"] = (2L * index) + 1, ["Title"] = titles[index], ["Body"] = "by name" });
        }

        for (var index = 0; index < titles.Length; index++)
        {
            long[] both = [2L * index, (2L * index) + 1];

            Assert.Equal(both, IdsOf(notes.Index("ByTitle").Find(IndexKey.Equal(titles[index]))));
            Assert.Equal(both, IdsOf(records.Index("ByTitle").Find(IndexKey.Equal(titles[index]))));
            Assert.All(notes.Index("ByTitle").Find(IndexKey.Equal(titles[index])), note => Assert.Equal(titles[index], note.Title));

            // Another spelling of the same text by the case table, which differs from it wherever the
            // title has a letter with a case.
            var other = CaseRule.ToUpper(titles[index]);

            if (other != titles[index])
            {
                var found = IdsOf(notes.Index("ByTitle").Find(IndexKey.Equal(other)));

                Assert.Equal(comparison == "OrdinalIgnoreCase" ? both : Array.Empty<long>(), found);
            }
        }

        Assert.Empty(notes.Index("ByTitle").Find(IndexKey.Equal("no such title")));
        Assert.Empty(records.Index("ByTitle").Find(IndexKey.Equal(titles[^1] + "x")));
    }

    [Theory]
    [MemberData(nameof(FormsWithoutCase))]
    public async Task Replace_that_keeps_an_equal_key_under_another_spelling_is_found_and_returns_the_new_spelling(string encoding, string storage)
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaOf(encoding, "OrdinalIgnoreCase", storage));
        var notes = database.Set<Note>();
        var records = database.Set("Note");
        var shortTitle = "ada lovelace é 𐐨";
        var longTitle = string.Concat(Enumerable.Repeat("ada lovelace é 𐐨 ", 8));

        await notes.InsertAsync(new Note { Id = 1, Title = shortTitle });
        await records.InsertAsync(new LatticeRecord(records.EntityType) { ["Id"] = 2L, ["Title"] = longTitle, ["Body"] = "" });

        await notes.ReplaceAsync(new Note { Id = 1, Title = CaseRule.ToUpper(shortTitle) });
        await records.ReplaceAsync(new LatticeRecord(records.EntityType) { ["Id"] = 2L, ["Title"] = CaseRule.ToUpper(longTitle), ["Body"] = "" });

        foreach (var spelling in new[] { shortTitle, CaseRule.ToUpper(shortTitle) })
        {
            Assert.Equal(CaseRule.ToUpper(shortTitle), Assert.Single(notes.Index("ByTitle").Find(IndexKey.Equal(spelling))).Title);
            Assert.Equal(CaseRule.ToUpper(shortTitle), Assert.Single(records.Index("ByTitle").Find(IndexKey.Equal(spelling)))["Title"]);
        }

        foreach (var spelling in new[] { longTitle, CaseRule.ToUpper(longTitle) })
        {
            Assert.Equal(CaseRule.ToUpper(longTitle), Assert.Single(notes.Index("ByTitle").Find(IndexKey.Equal(spelling))).Title);
            Assert.Equal(CaseRule.ToUpper(longTitle), Assert.Single(records.Index("ByTitle").Find(IndexKey.Equal(spelling)))["Title"]);
        }
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Replace_that_changes_a_text_key_is_found_under_the_new_key_and_a_delete_takes_it_away(string encoding, string comparison, string storage)
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaOf(encoding, comparison, storage));
        var notes = database.Set<Note>();
        var records = database.Set("Note");
        var head = new string('h', 90);
        string[] states = ["short", head + " first", "short again", head + " first", head + " second", "last"];

        await notes.InsertAsync(new Note { Id = 1, Title = states[0] });

        for (var state = 1; state < states.Length; state++)
        {
            if (state % 2 == 0)
            {
                await notes.ReplaceAsync(new Note { Id = 1, Title = states[state] });
            }
            else
            {
                await records.ReplaceAsync(new LatticeRecord(records.EntityType) { ["Id"] = 1L, ["Title"] = states[state], ["Body"] = "" });
            }

            Assert.Equal(1, Assert.Single(notes.Index("ByTitle").Find(IndexKey.Equal(states[state]))).Id);
            Assert.Empty(records.Index("ByTitle").Find(IndexKey.Equal(states[state - 1])));
        }

        await records.DeleteAsync(1L);
        Assert.Empty(notes.Index("ByTitle").Find(IndexKey.Equal(states[^1])));

        await records.InsertAsync(new LatticeRecord(records.EntityType) { ["Id"] = 1L, ["Title"] = head + " first", ["Body"] = "" });
        Assert.Equal(1L, Assert.Single(records.Index("ByTitle").Find(IndexKey.Equal(head + " first")))["Id"]);
        Assert.Empty(notes.Index("ByTitle").Find(IndexKey.Equal(head + " second")));
    }

    // The titles of the first test: the lengths in the encoding of the database are those the head of
    // the class names; they differ between the encodings for ASCII and agree for the two other kinds of
    // character, which take two and four bytes in both of them.
    private static string[] TitlesOf(string encoding)
    {
        var titles = new List<string> { "", "A", "Ada", "Ada Love", "Ada Lovelace and Charles Babbage" };

        // ASCII of 32, 33, 64, 65 and 100 characters, and of 5,000: as many bytes in UTF-8, twice as many
        // in UTF-16.
        foreach (var length in new[] { 32, 33, 64, 65, 100, 5_000 })
        {
            titles.Add(Letters(length));
        }

        // é takes two bytes and 𐐨 four in both encodings: 64 bytes and the next length past them.
        titles.Add(new string('é', 32));
        titles.Add(new string('é', 33));
        titles.Add(string.Concat(Enumerable.Repeat("𐐨", 16)));
        titles.Add(string.Concat(Enumerable.Repeat("𐐨", 17)));

        // Long keys that share their first bytes and differ at the end only, as numbered logins and
        // paths do: a key of 64 bytes and one of more that begins with it, and keys of 200 characters.
        var sixtyFourBytes = encoding == "Utf8" ? Letters(64) : Letters(32);

        titles.Add(sixtyFourBytes + "!");
        titles.Add(sixtyFourBytes + "?");

        for (var number = 0; number < 10; number++)
        {
            titles.Add($"{Letters(195)}{number:D5}");
        }

        Assert.Equal(titles.Count, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(titles, title => ByteCountOf(title, encoding) == 64);
        Assert.Contains(titles, title => ByteCountOf(title, encoding) is 65 or 66);

        return [.. titles];
    }

    // Lowercase and uppercase letters of ASCII in turn, so that the text has a case and another spelling.
    private static string Letters(int length) =>
        string.Create(length, 0, static (letters, _) =>
        {
            for (var index = 0; index < letters.Length; index++)
            {
                var letter = (char)('a' + (index % 26));

                letters[index] = index % 2 == 0 ? letter : char.ToUpperInvariant(letter);
            }
        });

    private static int ByteCountOf(string text, string encoding) =>
        encoding == "Utf8" ? Encoding.UTF8.GetByteCount(text) : Encoding.Unicode.GetByteCount(text);

    private static long[] IdsOf(IEnumerable<Note> found) => [.. found.Select(note => note.Id).Order()];

    private static long[] IdsOf(IEnumerable<LatticeRecord> found) => [.. found.Select(record => record.Get<long>("Id")).Order()];

    private static LatticeSchema SchemaOf(string encoding, string comparison, string storage) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note", "ClrType": "{{typeof(Note).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Title", "Type": "String", "Comparison": "{{comparison}}", "StringStorage": "{{storage}}" },
                { "Id": 3, "Name": "Body", "Type": "String" }
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

        public string Body { get; set; } = "";
    }
}
