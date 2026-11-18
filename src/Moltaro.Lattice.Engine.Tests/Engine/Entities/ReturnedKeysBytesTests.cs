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

    // The bytes of the places by the rule: 8 a word, and 8 for the one page that holds them, none when
    // there are no places yet.
    private static long BytesOf(long places, int wordsPerPlace) =>
        places == 0 ? 0 : (places * wordsPerPlace * sizeof(long)) + sizeof(long);
}
