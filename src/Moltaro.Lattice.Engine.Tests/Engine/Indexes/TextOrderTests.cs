using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.TestSupport.Text;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// Text in an index is ordered by the comparison of its field, the same in a UTF-8 and in a UTF-16
// database: Ordinal by the UTF-16 code units, without case by the code units of the texts mapped through
// the case table of the core (docs/design/ordered-index.md, "Entries and their order"). The bytes of UTF-8
// do not have that order: a character from U+E000 to U+FFFF comes after a surrogate pair in UTF-16 code
// units and before it in UTF-8 bytes. The same keys go into a database of each encoding, through Login,
// Plain and Ordinal, and Region, Dictionary and without case, each key short enough to be copied into an
// entry and long enough to be shared: in both databases the entries of each index lie in the order of
// its comparison and in the same order of keys, and every key is found with exactly the entities whose
// key its comparison holds equal.
public sealed class TextOrderTests
{
    // Characters on both sides of the places where UTF-8 and UTF-16 order differ, with a case and without.
    private static readonly string[] Characters =
    [
        "a", "A", "z", "é", "É", "퟿", "", "", "�", "￿",
        "\U00010000", "\U00010400", "\U00010428", "\U0001F600", "\U0010FFFD",
    ];

    [Fact]
    public async Task Keys_across_surrogate_pairs_and_the_last_plane_characters_lie_in_one_order_in_a_UTF8_and_a_UTF16_database()
    {
        var keys = KeysOf();

        // The premise: UTF-8 bytes order two of the keys otherwise than UTF-16 code units do.
        Assert.True(Math.Sign(string.CompareOrdinal("k", "k\U00010000")) != Math.Sign(Encoding.UTF8.GetBytes("k").AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes("k\U00010000"))));

        await using var utf8 = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        await using var utf16 = await EngineMaster.OpenAsync(IndexSchema.Utf16);

        foreach (var database in new[] { utf8, utf16 })
        {
            for (var id = 0; id < keys.Length; id++)
            {
                await database.Set<IndexSchema.Ranked>().InsertAsync(new IndexSchema.Ranked { Id = id, Number = id, Login = keys[id], Region = keys[id] });
            }

            IndexTally.ExpectEntries(database, "Ranked", "ByLogin", keys.Select((key, id) => ((object?)key, (long)id)), (left, right) => string.CompareOrdinal((string)left!, (string)right!));
            IndexTally.ExpectEntries(database, "Ranked", "ByRegion", keys.Select((key, id) => ((object?)key, (long)id)), (left, right) => string.CompareOrdinal(CaseRule.ToUpper((string)left!), CaseRule.ToUpper((string)right!)));

            foreach (var key in keys)
            {
                long[] exactly = [.. Enumerable.Range(0, keys.Length).Where(id => keys[id] == key).Select(id => (long)id)];
                long[] withoutCase = [.. Enumerable.Range(0, keys.Length).Where(id => CaseRule.ToUpper(keys[id]) == CaseRule.ToUpper(key)).Select(id => (long)id)];

                Assert.Equal(exactly, database.Set<IndexSchema.Ranked>().Index("ByLogin").Find(IndexKey.Equal(key)).Select(found => found.Id).Order());
                Assert.Equal(withoutCase, database.Set<IndexSchema.Ranked>().Index("ByRegion").Find(IndexKey.Equal(key)).Select(found => found.Id).Order());
            }
        }

        Assert.Equal(KeysInOrder(utf8, "ByLogin", key => key), KeysInOrder(utf16, "ByLogin", key => key));
        Assert.Equal(KeysInOrder(utf8, "ByRegion", CaseRule.ToUpper), KeysInOrder(utf16, "ByRegion", CaseRule.ToUpper));
    }

    // Each character between a head and a tail, short, 64 bytes or less in both encodings, and long, more
    // than 64 bytes in both; and a key of each pair of characters, so that the order of the second
    // character is seen after an equal first one.
    private static string[] KeysOf()
    {
        var keys = new List<string>();

        foreach (var first in Characters)
        {
            keys.Add($"k{first}x");
            keys.Add($"{new string('L', 70)}{first}x");

            foreach (var second in Characters)
            {
                keys.Add($"p{first}{second}");
            }
        }

        Assert.All(keys.Where(key => key.Length < 10), key => Assert.True(Encoding.Unicode.GetByteCount(key) <= 64));
        Assert.All(keys.Where(key => key.Length > 70), key => Assert.True(Encoding.UTF8.GetByteCount(key) > 64));

        return [.. keys];
    }

    // The keys of the entries of the index in their order, as the comparison of the index sees them.
    private static List<string> KeysInOrder(EngineDatabase database, string index, Func<string, string> asCompared) =>
        [.. IndexContents.Of(database, "Ranked", index).Entries.Select(entry => asCompared((string)entry.Key[0]!))];
}
