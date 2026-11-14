namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

internal static class EntryData
{
    public const ulong UpdateMask = 0x5a5a5a5a5a5a5a5a;

    public static RootEntry Create(int index) => new()
    {
        Location = ((ulong)(uint)index << 12) | 123,
        Revision = index + 17,
        Generation = (uint)index + 1,
        AccessState = index & 3,
        Retainers = index & 7,
        NextFree = -1
    };

    public static int[] CreateTrace(int count)
    {
        var trace = Enumerable.Range(0, count).ToArray();
        new Random(20261113).Shuffle(trace);
        return trace;
    }

    public static void Initialize<TAccess>(TAccess access, int count) where TAccess : struct, IRootAccess
    {
        for (var index = 0; index < count; index++)
        {
            access.Get(index) = Create(index);
        }
    }

    public static void Verify<TAccess>(TAccess access, int count, int updatePasses)
        where TAccess : struct, IRootAccess
    {
        for (var index = 0; index < count; index++)
        {
            var expected = Create(index);
            expected.Revision += updatePasses;
            if ((updatePasses & 1) != 0)
            {
                expected.Location ^= UpdateMask;
            }

            var actual = access.Get(index);
            if (actual.Location != expected.Location || actual.Revision != expected.Revision
                || actual.Generation != expected.Generation || actual.AccessState != expected.AccessState
                || actual.Retainers != expected.Retainers || actual.NextFree != expected.NextFree)
            {
                throw new InvalidOperationException($"Entry {index} differs from its expected state.");
            }
        }
    }
}
