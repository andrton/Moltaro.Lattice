using System.Globalization;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation;

// What the tests of the bound by levels share, LevelBoundTests and TightLevelBoundTests: an entity type of two
// texts beside those of IndexSchema, the entries of keys that order by a number, as wide as a case chooses,
// and what the publications of an index took from its arena and retired since a moment.
internal static class LevelBoundParts
{
    // An entity type of two texts, with an index of the first and one of both: the credits of their leaves
    // are 5 and 2, as those of the users of the reference fixture by login and by region and login.
    public static LatticeSchema Paired { get; } = IndexSchema.With("Utf8", """
        {
          "Id": 30, "Name": "Paired",
          "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "First", "Type": "String" }, { "Id": 3, "Name": "Second", "Type": "String" } ],
          "PrimaryKey": [1],
          "Indexes": [ { "Id": 1, "Name": "ByFirst", "Fields": [2] }, { "Id": 2, "Name": "ByPair", "Fields": [2, 3] } ]
        },
        """);

    // A key that orders by the number given, as wide as given: the number in ten digits and as many x after.
    public static string KeyOf(long number, int width) =>
        number.ToString("D10", CultureInfo.InvariantCulture).PadRight(width, 'x');

    // The entry of a new root of the entity type under the identity given, whose key orders by the key
    // given: the number of the key in Number for Counted, the key itself in the first text, and texts of
    // their own, as wide as the key, in the others.
    public static byte[] EntryOf(RootArena roots, SecondaryIndex index, RootHandle identity, string key, Random random)
    {
        var writer = roots.Writer();

        writer.SetInteger(roots.Field("Id"), identity.Slot);

        foreach (var field in roots.EntityType.Fields.Where(field => field.Name != "Id"))
        {
            if (field.Name == "Number")
            {
                writer.SetInteger(field, long.Parse(key.AsSpan(0, 10), CultureInfo.InvariantCulture));
            }
            else
            {
                writer.SetString(field, field.Name == "First" ? key : KeyOf(random.Next(100_000), key.Length));
            }
        }

        return IndexEntries.EntryOf(roots, index.Layout, roots.Publish(writer.Complete()), identity);
    }

    // The nodes the publications of the index took and retired since the counters of its arena given, read
    // from the arena, not from the pool: each node a publication takes is a prepared block made live, which
    // stays live or is retired by a later publication, and nothing is drained meanwhile, so the live and
    // retired bytes of the arena grow by the nodes taken, and its retired bytes by the nodes retired. The
    // roots of the entries lie in an arena of their own, so that an entry can be made at any moment without
    // changing what is counted.
    public static (long Taken, long Retired) TakenSince(Arena arena, IndexLayout layout, ArenaCounters before)
    {
        var nodeLive = BlockHeader.Size + layout.PayloadBytes;
        var nodeSlot = layout.NodeBytes;
        var counters = arena.Counters;
        var liveGrowth = counters.LiveBytes - before.LiveBytes;
        var retiredGrowth = counters.RetiredBytes - before.RetiredBytes;

        Assert.True(retiredGrowth % nodeSlot == 0, $"{retiredGrowth} retired bytes are not whole nodes.");

        var retiredNodes = retiredGrowth / nodeSlot;

        Assert.True((liveGrowth + (retiredNodes * nodeLive)) % nodeLive == 0, $"{liveGrowth} live bytes and {retiredNodes} retired nodes are not whole nodes.");

        return ((liveGrowth + (retiredNodes * nodeLive)) / nodeLive, retiredNodes);
    }
}
