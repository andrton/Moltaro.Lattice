using Arc.Collections;
using Faster.Map.Core;
using System.Runtime.Intrinsics;

namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal static class IntegrationChecks
{
    public static void Run()
    {
        CheckPlatform();
        NativeTextChecks.Run<ArcSlimIndex<NativeTextKey>>();
        NativeTextChecks.Run<DenseIndex<NativeTextKey>>();
        CheckDeletion<ArcSlimIndex<CollisionKey>>(expectAllocation: false);
        CheckDeletion<DenseIndex<CollisionKey>>(expectAllocation: true);
        ReservationChecks.Run();
        Console.WriteLine("All bounded integration checks passed. No database or production arena was used.");
    }

    public static void CheckPlatform()
    {
        var arc = new UnorderedMapSlim<long, ulong>(16);
        Require(arc.TryAdd(1, 7) && arc.TryGetValue(1, out var value) && value == 7,
            "Arc platform check failed.");
        var denseConstructed = false;
        try
        {
            _ = new DenseMap<long, ulong>(16);
            denseConstructed = true;
        }
        catch (NotSupportedException) when (!Vector256.IsHardwareAccelerated)
        {
            // A constructor failure is the behavior under test, not a fallback.
        }

        Require(denseConstructed == Vector256.IsHardwareAccelerated, "Unexpected Dense hardware behavior.");
        Console.WriteLine($"Vector256 accelerated: {Vector256.IsHardwareAccelerated}; Arc usable: True; Dense constructed: {denseConstructed}.");
    }

    private static void CheckDeletion<TIndex>(bool expectAllocation)
        where TIndex : struct, IPrimaryIndex<TIndex, CollisionKey>
    {
        var map = TIndex.Create(1024);
        for (var key = 0; key < 600; key++)
        {
            map.Insert(new CollisionKey(key), (ulong)key + 1);
        }

        // Warm the remove path with an absent key before measuring maintenance.
        Require(!map.Remove(new CollisionKey(-1)), "Absent collision key was removed.");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var key = 0; key < 200; key++)
        {
            Require(map.Remove(new CollisionKey(key)), "Collision-chain deletion failed.");
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require((allocated > 0) == expectAllocation, "Deletion allocation differed from inspected source.");
        Require(map.Count == 400 && map.Capacity == 1024, "Deletion unexpectedly changed count or capacity.");
        for (var key = 0; key < 600; key++)
        {
            var present = map.TryGet(new CollisionKey(key), out var value);
            Require(present == (key >= 200) && (!present || value == (ulong)key + 1),
                "Collision-chain contents were corrupted.");
        }

        Console.WriteLine($"{typeof(TIndex).Name}: 600 colliding keys, 200 removals; allocated {allocated} B; remaining values verified.");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
