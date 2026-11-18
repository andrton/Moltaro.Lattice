using System.Globalization;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.TestSupport.Skips;

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

    // The String keys of the tests of chunks: longer than the longest chunk a pass grows, and as many as
    // double the places twice.
    private const int LongKey = 40_000;

    private const int LongKeys = 25;

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

    // The text of the String keys a pass remembers lies in chunks, each key whole in one chunk, 2 bytes a
    // code unit, and their list holds a reference of 8 bytes for each place it has. Room for a key is made
    // in an order, and what is counted is the most that is live at any moment of it, not the sum of
    // everything it allocates: first the places double, while every key moves from the old places, which
    // are let go of then; then, when every place of the list of chunks is taken, the references are copied
    // into a list of twice the places, one for the first chunk, and the old list is let go of; then the
    // chunk is taken. So a key that doubles the places and needs a chunk counts the larger of the two
    // growths, never the old places with the new chunk, and a key that fills the list counts the old list
    // or the new chunk, whichever is larger, beside the new list. A key longer than the longest chunk the
    // pass grows has a chunk of its own exactly as long as its text, so with keys of 40,000 code units
    // every key takes a new chunk of its length; 25 keys double the places twice (ReturnedKeys.PlacesFor)
    // and fill the list five times. Every key whose room is not counted so is named.
    [Fact(Skip = SkipReason.BeforeTheFixesAfterSprint13)]
    public void Room_for_a_String_key_counts_the_most_that_is_live_at_once_while_the_places_the_list_of_chunks_and_the_chunk_grow()
    {
        using var standalone = new StandaloneKeys("String", LongKey);
        var keys = standalone.NewReturnedKeys();
        var miscounted = new List<string>();
        var held = new KeysHeld(0, 0, 0);

        for (var number = 1L; number <= LongKeys; number++)
        {
            var hash = standalone.Write(number);
            var stored = standalone.Stored;
            var growth = held.GrowthFor(number);
            var counted = keys.BytesWithRoomFor(LongKey);

            if (counted != growth.Live)
            {
                miscounted.Add($"key {number} ({growth.What}): counts {counted}, {growth.Live} are live at once");
            }

            Assert.False(keys.HasRoomFor(stored, out var bound));

            keys.MakeRoom(bound);
            keys.Stage(hash, stored, bound);
            keys.Commit();

            held = growth.After;
        }

        Assert.True(miscounted.Count == 0, $"Room not counted as the most that is live at once: {string.Join("; ", miscounted)}.");
    }

    // The same through the memory limit of a read of a database: a limit exactly at the most that is live
    // at once while room is made for the first key that doubles the places and needs a chunk lets the pass
    // return that key, and the next one, which needs one chunk more than the limit holds, fails it with
    // ReadMemory. A count of the old places with the new chunk would fail it one entity earlier.
    [Fact(Skip = SkipReason.BeforeTheFixesAfterSprint13)]
    public async Task Memory_limit_at_the_most_live_while_a_key_doubles_the_places_and_needs_a_chunk_lets_the_pass_return_that_key()
    {
        var held = new KeysHeld(0, 0, 0);
        var doubling = 0L;
        var limit = 0L;

        for (var number = 1L; doubling == 0; number++)
        {
            var growth = held.GrowthFor(number);

            if (number > 1 && growth.After.Places != held.Places)
            {
                doubling = number;
                limit = growth.Live;
            }

            held = growth.After;
        }

        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = KeySchema.Of("Utf8", "Ordinal", "String"),
            ReadMemoryLimit = limit,
        });
        var records = database.Set("String");

        for (var number = 1L; number <= doubling + 1; number++)
        {
            await records.InsertAsync(KeySchema.RecordOf(records, KeyOf(number), "body"));
        }

        var returned = 0;
        var failure = Record.Exception(() =>
        {
            foreach (var record in records.Enumerate())
            {
                returned++;
            }
        });

        Assert.Equal(LatticeLimitEnum.ReadMemory, Assert.IsType<LatticeException>(failure).Limit);
        Assert.True(returned == doubling, $"Under a limit of {limit} bytes the pass returned {returned} entities before it failed; that limit is the most that is live at once while it makes room for key {doubling}.");
    }

    // The key of the number in these tests, as StandaloneKeys writes it: the number followed by as many k
    // as make LongKey code units.
    private static string KeyOf(long number) =>
        string.Create(LongKey, number, static (units, value) =>
        {
            units.Fill('k');
            value.TryFormat(units, out _, default, CultureInfo.InvariantCulture);
        });

    // What the keys of LongKey code units a pass remembers hold, by the rule of the tests above: the
    // places, two words each, the chunks, every key in a chunk of its own, and the places of their list.
    private sealed record KeysHeld(long Places, long Chunks, long List)
    {
        // What making room for the key of the number does: what grows, the most that is live at once at a
        // moment of it, and what is held afterwards.
        public (string What, long Live, KeysHeld After) GrowthFor(long number)
        {
            var places = ReturnedKeys.PlacesFor(number, Places);
            var list = Chunks == List ? Math.Max(1, 2 * List) : List;
            var grownPlaceBytes = BytesOf(places, 2);
            var chunkBytes = 2L * LongKey * Chunks;

            // The moments: the places grow beside the old ones; the list grows beside the old one; the
            // chunk is taken. A moment that does not come counts nothing.
            var placesGrow = places == Places ? 0 : BytesOf(Places, 2) + grownPlaceBytes + chunkBytes + (8L * List);
            var listGrows = list == List ? 0 : grownPlaceBytes + chunkBytes + (8L * List) + (8L * list);
            var chunkTaken = grownPlaceBytes + chunkBytes + (8L * list) + (2L * LongKey);
            var what = (places == Places ? "" : "places double, ") + (list == List ? "" : "list grows, ") + "chunk";

            return (what, Math.Max(placesGrow, Math.Max(listGrows, chunkTaken)), new KeysHeld(places, Chunks + 1, list));
        }
    }

    // The bytes of the places by the rule: 8 a word, and 8 for the one page that holds them, none when
    // there are no places yet.
    private static long BytesOf(long places, int wordsPerPlace) =>
        places == 0 ? 0 : (places * wordsPerPlace * sizeof(long)) + sizeof(long);
}
