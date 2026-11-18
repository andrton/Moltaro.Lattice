using Moltaro.Lattice.Engine.Entities;

namespace Moltaro.Lattice.Engine.Tests.Engine.Entities;

// What the memory limit of a read counts before a pass makes room for one more key: the temporary memory
// that is live at once (decision on queries, docs/decisions/queries.md, the temporary memory of a read:
// "sum simultaneously live buffers", "check before growing workspace, accounting for buffer capacity").
// The keys a pass remembers are counted by the elements of the arrays that hold them (ReturnedKeys): 8
// bytes a word of a place, a word for an Int64 key and two for a Guid, and 8 bytes a reference of the list
// of pages, one page for the places of these tests. A key that fits the places counts what they hold. A
// key that doubles them moves every key from the old places into the new ones, and the old places are
// live until all are moved: it counts the old places with the new. The test drives ReturnedKeys as a step
// of a pass does (StandaloneKeys), for an Int64 and a Guid key, through the first five tables, and knows
// when the places grow from the growth function alone (ReturnedKeys.PlacesFor).
public sealed class ReturnedKeysBytesTests
{
    private const int Tables = 5;

    [Theory]
    [InlineData("Int64")]
    [InlineData("Guid")]
    public void Room_for_a_key_that_doubles_the_places_counts_the_old_places_with_the_new(string keyType)
    {
        using var standalone = new StandaloneKeys(keyType);
        var keys = standalone.NewReturnedKeys();
        var words = standalone.WordsPerPlace;
        var places = 0L;
        var tables = 0;

        for (var number = 1L; tables < Tables; number++)
        {
            var hash = standalone.Write(number);
            var stored = standalone.Stored;
            var needed = ReturnedKeys.PlacesFor(number, places);
            var live = needed == places ? BytesOf(places, words) : BytesOf(places, words) + BytesOf(needed, words);

            Assert.True(
                keys.BytesWithRoomFor(0) == live,
                $"Room for key {number} of {keyType}, which takes {needed} places beside the {places} there are, counts {keys.BytesWithRoomFor(0)} bytes; {live} are live at once.");

            if (!keys.HasRoomFor(stored, out var bound))
            {
                keys.MakeRoom(bound);
            }

            keys.Stage(hash, stored, bound);
            keys.Commit();

            if (needed != places)
            {
                tables++;
                places = needed;
            }
        }
    }

    // The text of the String keys a pass remembers lies in chunks, each key whole in one chunk, and their
    // list holds a reference of 8 bytes for each place it has; 2 bytes a code unit of a chunk. When a key
    // needs a chunk and every place of the list is taken, the references are copied into a list of twice
    // the places, one for the first chunk, and the old list is live until they are: the room for that key
    // counts the old list with the new, besides the chunks held and the new chunk. A key longer than the
    // longest chunk the pass grows has a chunk of its own exactly as long as its text, so with keys of
    // 40,000 code units every key takes a new chunk of its length. Nine keys fill the list four times; the
    // places of the keys, two words each, are those of the first table throughout.
    [Fact]
    public void Room_for_a_String_key_that_needs_a_chunk_when_the_list_of_chunks_is_full_counts_the_old_list_with_the_new()
    {
        const int Length = 40_000;
        const int Keys = 9;

        using var standalone = new StandaloneKeys("String", Length);
        var keys = standalone.NewReturnedKeys();
        var places = ReturnedKeys.PlacesFor(1, 0);
        var chunks = 0;
        var list = 0;

        for (var number = 1L; number <= Keys; number++)
        {
            var hash = standalone.Write(number);
            var stored = standalone.Stored;
            var held = (number == 1 ? 0 : BytesOf(places, 2)) + (2L * Length * chunks) + (8L * list);
            var grown = chunks == list ? Math.Max(1, 2 * list) : list;
            var live = held + (number == 1 ? BytesOf(places, 2) : 0) + (2L * Length) + (grown == list ? 0 : 8L * grown);

            Assert.True(
                keys.BytesWithRoomFor(Length) == live,
                $"Room for key {number}, which needs a chunk beside {chunks} with a list of {list} places, counts {keys.BytesWithRoomFor(Length)} bytes; {live} are live at once.");
            Assert.False(keys.HasRoomFor(stored, out var bound));

            keys.MakeRoom(bound);
            keys.Stage(hash, stored, bound);
            keys.Commit();

            chunks++;
            list = grown;
        }
    }

    // The bytes of the places by the rule: 8 a word, and 8 for the one page that holds them, none when
    // there are no places yet.
    private static long BytesOf(long places, int wordsPerPlace) =>
        places == 0 ? 0 : (places * wordsPerPlace * sizeof(long)) + sizeof(long);
}
