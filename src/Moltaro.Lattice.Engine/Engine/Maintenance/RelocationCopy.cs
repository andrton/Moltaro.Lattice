using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Maintenance;

// Under the writes lock, prepares and publishes an unreachable copy in a slot
// already found by Arena.TryFindRelocationClass. A success reserves one retirement
// so the caller can redirect the owner and enqueue the old slot without allocating
// (docs/design/arena-protocol.md, "Deferred reclamation").
internal static class RelocationCopy
{
    public static bool TryPublish(Arena arena, RetiredMemory retired, ArenaAddress source, int sizeClass,
        out ArenaAddress replacement)
    {
        replacement = default;
        if (!retired.TryReserveRelocated())
        {
            return false;
        }

        try
        {
            replacement = arena.PrepareRelocatedCopy(source, sizeClass);
            arena.Publish(replacement);
            return true;
        }
        catch
        {
            retired.Release();
            if (!replacement.IsNull)
            {
                arena.Free(replacement);
            }

            throw;
        }
    }
}
