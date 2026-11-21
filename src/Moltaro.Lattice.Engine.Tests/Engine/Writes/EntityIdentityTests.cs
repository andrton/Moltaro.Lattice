using Moltaro.Lattice.Access;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// A write to an entity that has a prepared, unpublished write waits behind it and holds nothing
// meanwhile (milestone contract, "Write coordinator and revision semantics"), and its entity is its
// entity type and its key as the schema compares keys: two spellings of a String key compared without
// case are one entity, so a write of one spelling waits behind a write of the other as behind any
// write of its entity. The persistence step of the test (HeldPersistence) holds an insert of "abcdef";
// an insert of "ABCDEF" admitted after it is not prepared, while an insert of another key admitted
// after both is; once the first is published, the second finds the entity and is DuplicateKey, and the
// entity keeps the spelling it was inserted with.
// In a database of UTF-8 and in one of UTF-16 (KeySchema).
public sealed class EntityIdentityTests
{
    [Theory]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public async Task Write_of_another_spelling_of_a_key_compared_without_case_waits_behind_the_write_of_the_first_spelling(string encoding)
    {
        var storage = new HeldPersistence();
        var database = await EngineMaster.OpenAsync(KeySchema.Of(encoding, "OrdinalIgnoreCase", "String"), new EngineTestInputs { Persistence = storage });
        var records = database.Set("String");

        try
        {
            var first = records.InsertAsync(KeySchema.RecordOf(records, "abcdef", "first")).AsTask();
            await storage.NextAsync();

            var one = database.Counters.Arena.PreparedBytes;
            var spelling = records.InsertAsync(KeySchema.RecordOf(records, "ABCDEF", "second")).AsTask();

            // Another key, of the same length and value: prepared, unlike the spelling admitted before it.
            var other = records.InsertAsync(KeySchema.RecordOf(records, "ghijkl", "first")).AsTask();
            var counters = await CountersWait.UntilAsync(database, counters => counters.Arena.PreparedBytes >= 2 * one, "the insert of another key is prepared");

            Assert.Equal(2 * one, counters.Arena.PreparedBytes);
            Assert.Equal(3, counters.QueuedWrites);

            storage.ReleaseAll();

            Assert.True((await first.WaitAsync(Bound)).Changed);
            Assert.True((await other.WaitAsync(Bound)).Changed);

            var duplicate = await Assert.ThrowsAsync<LatticeException>(() => spelling.WaitAsync(Bound));

            Assert.Equal(LatticeErrorEnum.DuplicateKey, duplicate.Error);
            Assert.Equal("abcdef", records.Get("ABCDEF")?["Id"]);
            Assert.Equal("first", records.Get("ABCDEF")?["Body"]);
            Assert.Equal(2, database.Counters.RootEntries);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }
}
