namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// What the text table of a database holds, as the engine shows it to its tests (exception X3): each entry
// with the exact bytes it holds and its count of references (EngineDatabase.TextReferences), and the
// number of entries (EngineCounters.TextEntries); both are taken under the lock of the writes, between two
// changes of the engine. A test compares them with its own tally (TextTally), the statement of point 9 of
// the planner's decision after the study of S13: the counts equal an independent tally of the
// descriptors in live blocks, in blocks retired and not yet freed, and in prepared blocks.
//
// Both members drain first (EngineDatabase.DrainRetired), so that a root a write replaced or deleted and
// no read can reach any more has been freed with its references, as the tally takes it to be; what a read
// the test holds can reach stays retired through the drain, and the tally counts it (TextTally.Retaining).
// Neither is called while a preparation is held, which holds the lock of the writes.
internal static class EntryCounts
{
    // The entries of the table by their text, decoded from the encoding of the database, and their
    // references.
    public static Dictionary<string, long> Of(EngineDatabase database, string encoding)
    {
        var decoding = TextTally.EncodingOf(encoding);
        var entries = new Dictionary<string, long>(StringComparer.Ordinal);

        database.DrainRetired();

        foreach (var (text, references) in database.TextReferences())
        {
            Assert.True(entries.TryAdd(decoding.GetString(text), references), "The text table holds one text in two entries.");
        }

        return entries;
    }

    // The table holds one entry for each text the tally refers to, with the references the tally counts,
    // and no other; its counter says how many.
    public static void ExpectEqual(TextTally tally, EngineDatabase database)
    {
        var expected = tally.References.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList();
        var actual = Of(database, tally.DatabaseEncoding).OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList();

        Assert.Equal(expected, actual);
        Assert.Equal(expected.Count, database.Counters.TextEntries);
    }
}
