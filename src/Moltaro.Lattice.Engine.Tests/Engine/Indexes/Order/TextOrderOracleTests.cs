using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Order;

// The order of a String part compared Ordinal is the order of ValueComparer.CompareText, the oracle
// (OracleTexts), whatever way the order is computed, in a UTF-8 and in a UTF-16 database (decision on
// queries, "String ordering and query overrides": UTF-8 and UTF-16 storage produce the same logical order,
// and physical byte ordering is not the contract). Held in three places, over the texts of OracleTexts,
// which go exhaustively through the code points around the surrogates and the supplementary characters:
// - IndexOrder.Compare of two entries, each copied from a root of 64 bytes or less, and of a search key
//   against an entry, each text of the exhaustive set against each representative and back, every pair
//   differing in its text, so that the text decides and not the identity;
// - the walk of a built index (EngineDatabase.IndexNodes), which holds every text, and each representative
//   also behind a head that makes it longer than 64 bytes and so a key shared with its text entry: each
//   entry is before the next by the oracle;
// - a search for every text, which finds exactly the entity that has it.
// A surrogate without its pair cannot be stored: the write that brings one is refused for its validation,
// which the walk states for one, so the texts hold surrogates only in their pairs.
public sealed class TextOrderOracleTests
{
    public static IEnumerable<TheoryDataRow<string>> Encodings => IndexSchema.Encodings;

    [Theory]
    [MemberData(nameof(Encodings))]
    public void Order_of_two_entries_and_of_a_search_key_against_an_entry_is_the_order_of_the_oracle(string encoding)
    {
        using var item = new RootArena(OrderSchema.Of(encoding), "Keyed");
        Assert.True(item.EntityType.TryGetIndex("ByText", out var definition));

        var layout = IndexLayout.Compile(definition, item.Layout);
        var order = new IndexOrder(layout, item.Arena);
        var exhaustive = OracleTexts.Exhaustive;
        var representatives = OracleTexts.Representatives;
        var exhaustiveEntries = exhaustive.Select((text, place) => EntryOf(item, layout, place, text)).ToList();
        var representativeEntries = representatives.Select((text, place) => EntryOf(item, layout, exhaustive.Count + place, text)).ToList();
        var exhaustiveBytes = exhaustive.Select(text => OracleTexts.StoredOf(text, layout)).ToList();
        var representativeBytes = representatives.Select(text => OracleTexts.StoredOf(text, layout)).ToList();
        Span<byte> scratch = stackalloc byte[SearchKey.ScratchBytes];
        var differ = new List<string>();
        var otherByBytes = 0;

        for (var place = 0; place < exhaustive.Count; place++)
        {
            for (var other = 0; other < representatives.Count; other++)
            {
                var text = exhaustive[place];
                var representative = representatives[other];
                var expected = OracleTexts.Order(exhaustiveBytes[place], representative, layout);

                Assert.NotEqual(0, expected);

                if (Math.Sign(exhaustiveBytes[place].AsSpan().SequenceCompareTo(representativeBytes[other])) != expected)
                {
                    otherByBytes++;
                }

                var entryOrder = Math.Sign(order.Compare(exhaustiveEntries[place], representativeEntries[other]));
                var backOrder = Math.Sign(order.Compare(representativeEntries[other], exhaustiveEntries[place]));
                var keyOrder = Math.Sign(order.Compare(new SearchKey(layout, IndexKey.Equal(text), default, scratch), representativeEntries[other]));
                var backKeyOrder = Math.Sign(order.Compare(new SearchKey(layout, IndexKey.Equal(representative), default, scratch), exhaustiveEntries[place]));

                if ((entryOrder, backOrder, keyOrder, backKeyOrder) != (expected, -expected, expected, -expected))
                {
                    differ.Add($"{Escaped(text)} against {Escaped(representative)}: the oracle {expected}, the entries {entryOrder} and {backOrder}, the keys {keyOrder} and {backKeyOrder}");
                }
            }
        }

        // The premise: the stored bytes of some of the pairs, compared as bytes, are in another order than the
        // oracle's, so that the test tells a plain comparison of bytes from the order.
        Assert.True(otherByBytes > 0, "No pair of the texts is in another order by its stored bytes.");
        Assert.Empty(differ);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Built_index_lies_in_the_order_of_the_oracle_and_a_search_finds_every_text(string encoding)
    {
        await using var database = await EngineMaster.OpenAsync(OrderSchema.Of(encoding));
        var keyed = database.Set("Keyed");
        var head = new string('L', 70);
        List<string> texts = [.. OracleTexts.Exhaustive, .. OracleTexts.Representatives, .. OracleTexts.Representatives.Select(text => head + text)];

        var refused = await Assert.ThrowsAsync<LatticeException>(async () => await keyed.InsertAsync(KeyedOf(keyed, -1, "p\uD800z")));

        Assert.Equal(LatticeErrorEnum.ValidationFailed, refused.Error);

        for (var id = 0; id < texts.Count; id++)
        {
            await keyed.InsertAsync(KeyedOf(keyed, id, texts[id]));
        }

        var entries = IndexContents.Of(database, "Keyed", "ByText").Entries;
        var layout = OracleTexts.LayoutOf(encoding);
        var outOfOrder = new List<string>();

        Assert.Equal(texts.Order(StringComparer.Ordinal), entries.Select(entry => (string)entry.Key[0]!).Order(StringComparer.Ordinal));

        for (var place = 1; place < entries.Count; place++)
        {
            var before = (string)entries[place - 1].Key[0]!;
            var after = (string)entries[place].Key[0]!;

            if (OracleTexts.Order(OracleTexts.StoredOf(before, layout), after, layout) >= 0)
            {
                outOfOrder.Add($"{Escaped(before)} before {Escaped(after)}");
            }
        }

        Assert.Empty(outOfOrder);

        var index = keyed.Index("ByText");
        var notFound = new List<string>();

        for (var id = 0; id < texts.Count; id++)
        {
            var found = index.Find(IndexKey.Equal(texts[id])).Select(record => record.Get<long>("Id")).ToList();

            if (found is not [var only] || only != id)
            {
                notFound.Add($"{Escaped(texts[id])}: {string.Join(", ", found)}");
            }
        }

        Assert.Empty(notFound);
    }

    // The entry of a root of Keyed with the text given, under the identity of the place given.
    private static byte[] EntryOf(RootArena item, IndexLayout layout, int place, string text)
    {
        var writer = item.Writer();

        writer.SetInteger(item.Field("Id"), place);
        writer.SetString(item.Field("Text"), text);

        var root = writer.Complete();
        var entry = new byte[layout.WidestEntryBytes];

        return entry[..layout.WriteEntry(item.Reader(root), new RootHandle(1, (uint)place), entry)];
    }

    private static LatticeRecord KeyedOf(ILatticeSet keyed, long id, string text) =>
        new(keyed.EntityType) { ["Id"] = id, ["Text"] = text };

    // The text with every character outside ASCII as its code units, for a message.
    private static string Escaped(string text) =>
        string.Concat(text.Select(unit => unit < 0x80 ? unit.ToString() : $"\\u{(int)unit:X4}"));
}
