using System.Reflection;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

// The independent inventory of what an instance holds, beside the charge of its budget: the native
// grants with their segment objects, which the allocator counts, and the arrays that the structures
// report, each sized here by the layout of a 64-bit runtime and never by the budget. At a point of
// rest — no write, no child transaction, no forwarded request and no batch builder alive — the
// charge is exactly that inventory and the headroom. While a builder or a write is alive, the charge
// lies between the inventory and the inventory with the headroom and the estimates in flight.
internal static class HeldInventory
{
    // An array is an object header and a method table, 8 bytes each, and its length, padded to 8
    // bytes; its elements follow and the whole is rounded up to 8 bytes.
    public const int ArrayHeaderBytes = 24;

    public static long ArrayBytes(Type element, int length) => Align(ArrayHeaderBytes + (long)length * ElementBytes(element));

    public static long ArrayBytes(HeldArray array) => ArrayBytes(array.ElementType, array.Length);

    // A reference or a pointer takes 8 bytes; a value type takes its own size.
    public static long ElementBytes(Type element) =>
        element.IsValueType && !element.IsPointer ? RuntimeHelpers.SizeOf(element.TypeHandle) : IntPtr.Size;

    // An object of a class is its header and method table and its instance fields, rounded up to
    // 8 bytes; the runtime orders the fields by size, so they leave no gap between them.
    public static long ObjectBytes(Type type)
    {
        var fields = 0L;
        for (var level = type; level is not null; level = level.BaseType)
        {
            foreach (var field in level.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                fields += field.FieldType.IsPointer ? IntPtr.Size : ElementBytes(field.FieldType);
            }
        }

        return Align(2L * IntPtr.Size + fields);
    }

    // One native grant: its bytes and its segment object. Its metadata arrays are among the arrays.
    public static long GrantBytes(int segmentBytes) => segmentBytes + ObjectBytes(typeof(ArenaSegment));

    // The metadata arrays of one segment of the given size: the regions and the three bitmaps of
    // their slots.
    public static long SegmentMetadataBytes(int segmentBytes)
    {
        var regions = segmentBytes / ArenaSegment.RegionSize;
        return ArrayBytes(typeof(ArenaRegion), regions) + 3 * ArrayBytes(typeof(ulong), regions * ArenaSegment.SlotWordsPerRegion);
    }

    public static long Native(HeldMemory held) => held.GrantedSegments * GrantBytes(held.SegmentBytes);

    public static long Arrays(HeldMemory held) => held.Arrays.Sum(ArrayBytes);

    public static long Inventory(HeldMemory held) => Native(held) + Arrays(held);

    // The arrays of one inspection that an earlier one did not have, counted with their repetitions.
    public static List<HeldArray> Added(IReadOnlyList<HeldArray> arrays, IReadOnlyList<HeldArray> earlier)
    {
        var remaining = earlier.GroupBy(array => array).ToDictionary(group => group.Key, group => group.Count());
        var added = new List<HeldArray>();
        foreach (var array in arrays)
        {
            if (remaining.TryGetValue(array, out var count) && count > 0)
            {
                remaining[array] = count - 1;
            }
            else
            {
                added.Add(array);
            }
        }

        return added;
    }

    // Holds a charged array so that between room and room + 7 bytes are left under the limit; the
    // bytes of an array are a multiple of 8.
    public static IDisposable LeaveAbout(EngineDatabase node, long room, out long left)
    {
        var limit = node.Counters.MemoryLimit;
        var array = (limit - node.InspectHeld().Charged - room) / 8 * 8;
        var held = node.HoldBudgetBacking(checked((int)(array - ArrayHeaderBytes)));
        left = limit - node.InspectHeld().Charged;
        Assert.InRange(left, room, room + 7);
        return held;
    }

    // At a point of rest: the native grants are the allocator's, and the charge is exactly the
    // inventory and the headroom.
    public static HeldMemory ExpectAtRest(EngineDatabase node, CountingNativeAllocator allocator)
    {
        var held = node.InspectHeld();
        Assert.Equal(allocator.OutstandingBytes, (long)held.GrantedSegments * held.SegmentBytes);
        Assert.Equal(Inventory(held) + held.Headroom, held.Charged);
        Assert.InRange(held.Peak, held.Charged, node.Counters.MemoryLimit);
        return held;
    }

    // While builders or writes are alive whose estimates the caller gives: never below the
    // inventory, and above it by at most the headroom and those estimates.
    public static HeldMemory ExpectInFlight(EngineDatabase node, CountingNativeAllocator allocator, long estimates)
    {
        var held = node.InspectHeld();
        Assert.Equal(allocator.OutstandingBytes, (long)held.GrantedSegments * held.SegmentBytes);
        var inventory = Inventory(held);
        Assert.InRange(held.Charged, inventory, inventory + held.Headroom + estimates);
        return held;
    }

    private static long Align(long bytes) => (bytes + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
}
