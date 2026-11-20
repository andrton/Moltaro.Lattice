using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Upkeep;

// The one entity of Optional of the tests of a null key (UpkeepSchema), on a master and on a child: its
// record, the keys of ByValue in its walk, and what a search of ByValue finds.
internal static class OptionalEntities
{
    // The entity 1 of Optional with the Value and the Label given, and the marker every entity has.
    public static LatticeRecord Of(ILatticeSet optional, long? value, string label)
    {
        var record = new LatticeRecord(optional.EntityType) { ["Id"] = 1L, ["Marker"] = UpkeepSchema.Marker, ["Label"] = label };

        if (value is { } number)
        {
            record["Value"] = number;
        }

        return record;
    }

    // The keys of the entries of ByValue, in the order of the walk (EngineDatabase.IndexNodes).
    public static List<long?> KeysOf(EngineDatabase database) =>
        [.. IndexContents.Of(database, "Optional", "ByValue").Entries.Select(entry => (long?)entry.Key[0])];

    // The ids of the entities a search of ByValue by the key given finds, in ascending order.
    public static List<long> FoundBy(ILatticeSet optional, long? value) =>
        [.. optional.Index("ByValue").Find(IndexKey.Equal(value)).Select(found => found.Get<long>("Id")).Order()];
}
