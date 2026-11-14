namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal readonly record struct ValueSpec(int Key, long Number, int Seed, int Length, int TextGroup)
{
    private static readonly int[] Lengths = [128, 512, 1024, 1536];

    public static ValueSpec ForKey(int key, int cycle = 0) => new(
        key, key + cycle * 1000L, key + cycle * 31,
        Lengths[(key / 4 + cycle) % Lengths.Length], key % TextTable.Count);
}
