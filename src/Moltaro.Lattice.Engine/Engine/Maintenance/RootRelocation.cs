using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Maintenance;

// Moves a live root under the counted writes lock. The copy adopts its direct
// owned children before the root entry is release-published. A Text descriptor
// keeps the same dictionary reference. Earlier readers keep the old root
// through their epochs, and its raw FIFO entry never follows its children
// (docs/design/arena-protocol.md, "Deferred reclamation").
internal static class RootRelocation
{
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
        if (entry.Generation != expected.Generation || entry.State != RootStateEnum.Live || entry.Root != source)
        {
            return false;
        }

        var layout = parts.Tables.Stored(entry.TypeId).Layout;
        var arena = parts.Arena;
        if (!arena.TryFindRelocationDestination(source, region, out var sizeClass) ||
            !RelocationBlock.TryReadRoot(arena, layout, source, expected, out var holder) ||
            !RelocationChildren.Validate(arena, holder))
        {
            return false;
        }

        var retired = parts.Retired;
        if (!RelocationCopy.TryPublish(arena, retired, source, sizeClass, region, out replacement, out var slotBytes))
        {
            return false;
        }

        RelocationChildren.Adopt(arena, holder, replacement);

        // Publish the replacement before tagging the old slot. An epoch opened
        // after that tag can only reach the replacement or a later address.
        entry.ReleaseRoot(replacement);
        retired.RetireRelocated(source, slotBytes);
        return true;
    }
}
