using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Maintenance;

// Moves one fixed-only root under the writes lock of a counted maintenance step.
// The published handle, revision and position stay the same. Readers that already
// reached the old address hold it through their epochs (docs/design/arena-protocol.md).
internal static class RootRelocation
{
    public static bool TryMove(DatabaseParts parts, RootHandle expected, ArenaAddress source) =>
        TryMove(parts, expected, source, 0, out _);

    public static bool TryMove(DatabaseParts parts, RootHandle expected, ArenaAddress source, long region,
        out ArenaAddress replacement)
    {
        replacement = default;
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
        if (!arena.TryFindRelocationDestination(source, ref region, out var sizeClass) ||
            !RelocationCopy.TryPublish(arena, retired, source, sizeClass, region, out replacement, out var slotBytes))
        {
            return false;
        }

        // Publish the replacement before tagging the old slot. An epoch opened
        // after that tag can only reach the replacement or a later address.
        entry.ReleaseRoot(replacement);
        retired.RetireRelocated(source, slotBytes);
        return true;
    }
}
