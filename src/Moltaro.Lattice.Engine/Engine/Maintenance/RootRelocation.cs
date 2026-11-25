using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Maintenance;

// Moves one fixed-only root under the writes lock of a counted maintenance step.
// The published handle, revision and position stay the same. Readers that already
// reached the old address hold it through their epochs (docs/design/arena-protocol.md).
internal static class RootRelocation
{
    public static bool TryMove(DatabaseParts parts, RootHandle expected, ArenaAddress source)
    {
        // A master write can keep the physical source between preparation and
        // publication. A child's preparation and publication both hold this lock.
        if (parts.Coordinator.HoldsPreparedMemory() || expected.Slot >= (uint)parts.Roots.SlotExtent)
        {
            return false;
        }

        ref var entry = ref parts.Roots.EntryOfSlot(expected.Slot);
        if (entry.Generation != expected.Generation || entry.State != RootStateEnum.Live || entry.Root != source ||
            parts.Tables.Stored(entry.TypeId).Layout.DescriptorCount != 0)
        {
            return false;
        }

        var arena = parts.Arena;
        var retired = parts.Retired;
        if (!arena.TryFindRelocationClass(source, out var sizeClass) || !retired.TryReserveRelocated())
        {
            return false;
        }

        ArenaAddress replacement = default;
        try
        {
            replacement = arena.PrepareRelocatedCopy(source, sizeClass);
            arena.Publish(replacement);
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

        // Publish the replacement before tagging the old slot. An epoch opened
        // after that tag can only reach the replacement or a later address.
        entry.ReleaseRoot(replacement);
        retired.RetireRelocated(source);
        return true;
    }
}
