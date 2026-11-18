namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// The entities of the check under change and the key each has for the whole run, by its key number,
// whose order is the order of the keys:
// - key 0, the hot key, with HotIds identities;
// - keys 1 to NormalKeys, each with IdsPerKey identities;
// - the extra keys above them, one identity each, which the writer appends in ascending order and
//   takes out again.
// An entity is stable when its identity is a multiple of three and it is not an extra: the writer puts
// it in before the run and never touches it, so every walk over its key must return it and every
// search for its key must find it. The others are the writer's to change.
internal sealed class CheckModel
{
    public const int HotIds = 6_000;

    public const int NormalKeys = 2_048;

    public const int IdsPerKey = 12;

    public const int ExtraKeys = 4_096;

    public CheckModel()
    {
        IdCount = HotIds + (NormalKeys * IdsPerKey) + ExtraKeys;
        KeyCount = 1 + NormalKeys + ExtraKeys;
        KeyOf = new int[IdCount + 1];
        StableBelow = new int[KeyCount + 1];

        var stablePerKey = new int[KeyCount];

        for (var id = 1; id <= IdCount; id++)
        {
            KeyOf[id] = id <= HotIds ? 0
                : id <= HotIds + (NormalKeys * IdsPerKey) ? 1 + ((id - HotIds - 1) % NormalKeys)
                : 1 + NormalKeys + (id - HotIds - (NormalKeys * IdsPerKey) - 1);

            if (IsStable((ulong)id))
            {
                stablePerKey[KeyOf[id]]++;
            }
        }

        for (var key = 0; key < KeyCount; key++)
        {
            StableBelow[key + 1] = StableBelow[key] + stablePerKey[key];
        }
    }

    public int IdCount { get; }

    public int KeyCount { get; }

    // The key number of each identity, 1 to IdCount.
    public int[] KeyOf { get; }

    // The stable entities whose key number is below each number.
    public int[] StableBelow { get; }

    public int FirstExtraId => HotIds + (NormalKeys * IdsPerKey) + 1;

    public bool IsStable(ulong id) => id % 3 == 0 && id < (ulong)FirstExtraId;

    public bool IsExtra(ulong id) => id >= (ulong)FirstExtraId;

    // The stable entities with a key number from lowest to highest, both included.
    public int StableBetween(int lowest, int highest) => StableBelow[highest + 1] - StableBelow[lowest];
}
