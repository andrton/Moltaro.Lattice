using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Entities;

// The primary index of an entity type with a String key compared with Ordinal (decision on index
// structures, "Primary-key index: hash table"): its hashing and equality follow the key semantics
// of the schema, and the index compares a key with the text stored in the root, so it holds no copy
// of the text. A key is found whatever its length, whether it lies in the root block, in a block of
// its own or in chunks (brief of S05, "Where a value goes"), and with characters of more than one
// byte; keys that differ in one code unit, case included, are different entities; text that is not
// well-formed is never stored, so such a key finds nothing. A read by a String key is coherent while
// the entity is replaced, as a read by an Int64 key is (brief of S06, Acceptance).
public sealed class TextKeyTests
{
    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note", "ClrType": "{{typeof(Note).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Name", "Type": "String" },
                { "Id": 2, "Name": "Counter", "Type": "Int64" },
                { "Id": 3, "Name": "Body", "Type": "String" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    // Keys inline, in a block of their own, in one chunk and in two, and the empty key; the long ones
    // with characters of two, three and four bytes.
    public static TheoryData<int> KeyLengths => [0, 10, 100, 4_097, 70_000];

    [Theory]
    [MemberData(nameof(KeyLengths))]
    public async Task Entity_is_found_replaced_and_deleted_by_its_String_key_wherever_the_key_lies(int length)
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var notes = database.Set<Note>();
        var records = database.Set("Note");
        // The other key differs in its first code unit only; beside the empty key it is "b".
        var key = KeyOf(length, 'a');
        var other = length == 0 ? "b" : 'b' + key[1..];

        await notes.InsertAsync(new Note { Name = key, Counter = 1, Body = "first" });
        await records.InsertAsync(new LatticeRecord(records.EntityType) { ["Name"] = other, ["Counter"] = 2L, ["Body"] = "other" });

        Assert.Equal("first", notes.Get(key)!.Body);
        Assert.Equal("other", notes.Get(other)!.Body);
        Assert.Equal(1L, records.Get(key)!["Counter"]);

        await notes.ReplaceAsync(new Note { Name = key, Counter = 3, Body = "replaced" });

        Assert.Equal("replaced", records.Get(key)!["Body"]);
        Assert.Equal("other", notes.Get(other)!.Body);

        Assert.True((await records.DeleteAsync(key)).Changed);
        Assert.Null(notes.Get(key));
        Assert.Equal("other", notes.Get(other)!.Body);
        Assert.Equal([other], notes.Enumerate().Select(note => note.Name));
    }

    [Fact]
    public async Task Keys_that_differ_in_one_code_unit_or_only_in_case_are_different_entities()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var notes = database.Set<Note>();
        string[] keys = ["Key", "key", "KEY", "Kez", "Ke", "Key ", "K€y", "Kéy", "Kéy", "K😀y"];

        for (var index = 0; index < keys.Length; index++)
        {
            await notes.InsertAsync(new Note { Name = keys[index], Counter = index, Body = "" });
        }

        for (var index = 0; index < keys.Length; index++)
        {
            Assert.Equal(index, notes.Get(keys[index])!.Counter);
        }

        Assert.Null(notes.Get("kEy"));
        Assert.Null(notes.Get("K"));
    }

    [Fact]
    public async Task Key_that_is_not_well_formed_is_never_stored_and_finds_nothing()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var notes = database.Set<Note>();
        var records = database.Set("Note");

        // A high surrogate without its low one, at the end and inside the text.
        foreach (var key in new[] { "a\uD83D", "\uD83Da", "\uDE00" })
        {
            await Assert.ThrowsAsync<LatticeException>(async () => await notes.InsertAsync(new Note { Name = key, Counter = 1, Body = "" }));

            Assert.Null(notes.Get(key));
            Assert.Null(records.Get(key));
        }

        // A key whose text, with replacement characters, would be the one stored.
        await notes.InsertAsync(new Note { Name = "a�", Counter = 1, Body = "" });

        Assert.Null(notes.Get("a\uD83D"));
        Assert.Equal(1, database.Counters.RootEntries);
    }

    [Fact]
    public async Task Every_String_key_is_found_after_the_index_grew()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var notes = database.Set<Note>();
        const int Count = 3_000;

        // Keys with a long common start, so that they differ only near their end.
        for (var index = 0; index < Count; index++)
        {
            await notes.InsertAsync(new Note { Name = NumberedKey(index), Counter = index, Body = "" });
        }

        for (var index = 0; index < Count; index++)
        {
            Assert.Equal(index, notes.Get(NumberedKey(index))!.Counter);
        }

        Assert.Equal(Count, notes.Enumerate().Count());
    }

    [Fact]
    public async Task Read_by_a_String_key_returns_one_written_state_while_the_entity_is_replaced()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var notes = database.Set<Note>();
        var records = database.Set("Note");

        // The key and the body lie in two chunks, so that both the comparison of the key and the copy
        // of the body read blocks that every replace frees.
        var key = KeyOf(70_000, 'k');
        await notes.InsertAsync(NoteOf(key, 0));

        await ReadersAndWriter.RunAsync(
            4,
            reader => reader % 2 == 0
                ? () => ExpectOneState(key, notes.Get(key))
                : () => ExpectOneState(key, records.Get(key)),
            async () =>
            {
                for (var counter = 1; counter <= 200; counter++)
                {
                    await notes.ReplaceAsync(NoteOf(key, counter));
                }
            });

        Assert.Equal(200, notes.Get(key)!.Counter);
    }

    private static Note NoteOf(string key, long counter) => new() { Name = key, Counter = counter, Body = BodyOf(counter) };

    private static string BodyOf(long counter) => string.Concat(Enumerable.Repeat($"{counter:D8}|", 9_000));

    private static void ExpectOneState(string key, Note? note)
    {
        Assert.NotNull(note);
        Assert.Equal(key, note.Name);
        Assert.Equal(BodyOf(note.Counter), note.Body);
    }

    private static void ExpectOneState(string key, LatticeRecord? record)
    {
        Assert.NotNull(record);
        Assert.Equal(key, record["Name"]);
        Assert.Equal(BodyOf(record.Get<long>("Counter")), record["Body"]);
    }

    // A key of the length in code units, from the letter and characters of more than one byte in
    // UTF-8 when it is long enough to hold them.
    private static string KeyOf(int length, char letter)
    {
        const string Wide = "é€😀";

        return length < 2 * Wide.Length ? new string(letter, length) : letter + Wide + new string(letter, length - 1 - Wide.Length);
    }

    private static string NumberedKey(int index) => new string('n', 200) + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Note
    {
        public string Name { get; set; } = "";

        public long Counter { get; set; }

        public string Body { get; set; } = "";
    }
}
