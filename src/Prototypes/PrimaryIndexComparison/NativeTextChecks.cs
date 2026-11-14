using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal static class NativeTextChecks
{
    public static void Run<TIndex>() where TIndex : struct, IPrimaryIndex<TIndex, NativeTextKey>
    {
        (string Left, string Right)[] pairs =
        [
            ("", ""), ("Alpha", "ALPHA"), ("abc", "abd"), ("a\0b", "A\0B"),
            ("caf\u00e9", "CAF\u00c9"), ("\u043f\u0440\u0438\u0432\u0435\u0442", "\u041f\u0420\u0418\u0412\u0415\u0422"),
            ("\u03a3", "\u03c2"), ("\u0130", "i"), ("I", "\u0131"),
            ("\u00df", "SS"), ("\u00e9", "e\u0301"), ("\U00010400", "\U00010428"),
            ("\U0001f642", "\U0001f642"), (new string('x', 1024), new string('x', 1024))
        ];

        long totalAllocated = 0;
        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
        {
            foreach (var pair in pairs)
            {
                using var stored = new NativeTextBuffer(pair.Left);
                using var probe = new NativeTextBuffer(pair.Right);
                var map = TIndex.Create(16);
                var storedKey = stored.Key(comparison);
                var probeKey = probe.Key(comparison);
                var expected = string.Equals(pair.Left, pair.Right, comparison);
                map.Insert(storedKey, 17);

                IntegrationChecks.Require(map.TryGet(probeKey, out var actual) == expected &&
                    (!expected || actual == 17), "Native UTF-8 lookup disagrees with .NET string semantics.");
                IntegrationChecks.Require(!expected || storedKey.GetHashCode() == probeKey.GetHashCode(),
                    "Equal native keys have different hashes.");

                _ = Probe<TIndex>(ref map, probeKey);
                var before = GC.GetAllocatedBytesForCurrentThread();
                var checksum = Probe<TIndex>(ref map, probeKey);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                totalAllocated += allocated;
                IntegrationChecks.Require(checksum == (expected ? 1700UL : 0), "Native lookup checksum failed.");
                IntegrationChecks.Require(allocated == 0, "Native lookup allocated managed memory.");

                // Repair the physical key descriptor before releasing old text.
                // This is single-threaded relocation, not the engine's reclamation protocol.
                using var replacement = new NativeTextBuffer(pair.Left);
                IntegrationChecks.Require(map.Remove(storedKey), "Native key removal failed.");
                map.Insert(replacement.Key(comparison), 19);
                stored.Dispose();
                IntegrationChecks.Require(map.TryGet(replacement.Key(comparison), out actual) && actual == 19,
                    "Relocated native key was not found.");
                map.Remove(replacement.Key(comparison));
            }
        }

        Console.WriteLine($"{typeof(TIndex).Name}: 28 UTF-8 equality/relocation cases; key {Unsafe.SizeOf<NativeTextKey>()} B; lookup allocation {totalAllocated} B.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong Probe<TIndex>(ref TIndex map, NativeTextKey key)
        where TIndex : struct, IPrimaryIndex<TIndex, NativeTextKey>
    {
        ulong sum = 0;
        for (var index = 0; index < 100; index++)
        {
            if (map.TryGet(key, out var value))
            {
                sum += value;
            }
        }

        return sum;
    }
}
