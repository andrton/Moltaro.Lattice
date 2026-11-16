using Moltaro.Lattice.Engine.Entities;

namespace Moltaro.Lattice.Engine.Tests.Engine.Entities;

// An enumeration of any length runs to its end (brief of Epic 1, sprint S08, Acceptance): the keys a
// pass remembers have room for every entity the table of root entries admits, fewer than 2^31
// (Storage.RootTable), so the places of ReturnedKeys grow through every count without overflowing and
// lie in arrays that their largest length does not cut short (review round 1 of S08, part 2). No test
// can hold that many entities, so this one follows the counts alone, through the helpers that
// ReturnedKeys computes its room with: from the first key to the last, at every doubling, for keys of
// one word and of two.
public sealed class ReturnedKeysCapacityTests
{
    // The most entities a table of root entries admits: fewer than 2^31.
    private const long MostKeys = int.MaxValue;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Places_hold_every_key_a_table_of_root_entries_admits_without_overflow(int wordsPerPlace)
    {
        var places = ReturnedKeys.PlacesFor(1, 0);
        var doublings = 0;

        while (true)
        {
            ExpectPagesOf(places, wordsPerPlace);

            // The first count the places do not hold.
            var beyond = (places / 4 * 3) + 1;

            if (beyond > MostKeys)
            {
                break;
            }

            Assert.Equal(places, ReturnedKeys.PlacesFor(beyond - 1, places));

            var grown = ReturnedKeys.PlacesFor(beyond, places);

            Assert.Equal(2 * places, grown);

            places = grown;
            doublings++;
        }

        // Three quarters of 2^32 places hold 2^31 keys.
        Assert.Equal(1L << 32, places);
        Assert.Equal(places, ReturnedKeys.PlacesFor(MostKeys, places));
        Assert.Equal(28, doublings);
    }

    [Fact]
    public void Small_table_is_one_array_of_its_places()
    {
        Assert.Equal((1, 16), ReturnedKeys.PagesFor(16, 1));
        Assert.Equal((1, 32), ReturnedKeys.PagesFor(16, 2));
        Assert.Equal((16 * sizeof(long)) + sizeof(long), ReturnedKeys.PlaceBytes(16, 1));
    }

    // The pages of the places are whole, hold every place, and each fits one array; their bytes are
    // counted without overflow.
    private static void ExpectPagesOf(long places, int wordsPerPlace)
    {
        var (pages, wordsPerPage) = ReturnedKeys.PagesFor(places, wordsPerPlace);

        Assert.InRange(pages, 1, int.MaxValue);
        Assert.InRange(wordsPerPage, 1, Array.MaxLength);
        Assert.Equal(places * wordsPerPlace, (long)pages * wordsPerPage);
        Assert.Equal((places * wordsPerPlace * sizeof(long)) + ((long)pages * sizeof(long)), ReturnedKeys.PlaceBytes(places, wordsPerPlace));
    }
}
