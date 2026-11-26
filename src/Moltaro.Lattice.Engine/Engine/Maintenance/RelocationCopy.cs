using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Engine.Maintenance;

// Under the writes lock, prepares and publishes an unreachable copy in a slot
// already found by the arena. A success reserves one retirement
// so the caller can redirect the owner and enqueue the old slot without allocating
// (docs/design/arena-protocol.md, "Deferred reclamation").
internal static class RelocationCopy
{
    public static bool TryPublish(Arena arena, RetiredMemory retired, ArenaAddress source, int sizeClass,
        long region, out ArenaAddress replacement, out int slotBytes)
    {
        replacement = default;
        slotBytes = SizeClasses.SizeOf(sizeClass);
        if (!retired.TryReserveRelocated(slotBytes))
        {
            return false;
        }

        try
        {
            replacement = arena.PrepareRelocatedCopy(source, sizeClass, region);
            arena.Publish(replacement);
            return true;
        }
        catch
        {
            if (!replacement.IsNull)
            {
                arena.Free(replacement);
            }

            retired.ReleaseRelocation(slotBytes);
            throw;
        }
    }
}
