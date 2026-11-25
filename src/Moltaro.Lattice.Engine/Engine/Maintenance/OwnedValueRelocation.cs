using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Maintenance;

// Moves one value with no owned descendants under the writes lock of a counted
// maintenance step. Only the live root's edge changes. Older roots may keep an alias
// to the original block, whose header and bytes stay available until their epochs end
// (docs/design/arena-protocol.md, "Deferred reclamation").
internal static class OwnedValueRelocation
{
    public static bool TryMove(DatabaseParts parts, RootHandle expected, ArenaAddress source) =>
        TryMove(parts, expected, source, 0, out _);

    public static bool TryMove(DatabaseParts parts, RootHandle expected, ArenaAddress source, long region,
        out ArenaAddress replacement)
    {
        replacement = default;
        // Prepared master writes can carry the old edge across the writes lock.
        // A child holds this lock through preparation and publication.
        if (parts.Coordinator.HoldsPreparedMemory() || expected.Slot >= (uint)parts.Roots.SlotExtent)
        {
            return false;
        }

        ref var entry = ref parts.Roots.EntryOfSlot(expected.Slot);
        var arena = parts.Arena;
        if (entry.Generation != expected.Generation || entry.State != RootStateEnum.Live ||
            !arena.TryFindRelocationDestination(source, ref region, out var sizeClass))
        {
            return false;
        }

        ref readonly var header = ref arena.Header(source);
        var root = entry.Root;
        var layout = parts.Tables.Stored(entry.TypeId).Layout;
        if (header.Kind != BlockKindEnum.Value || header.Root != expected || header.Parent != root ||
            header.LayoutVersion != layout.LayoutVersion)
        {
            return false;
        }

        if (!layout.TryGetDescriptorIndex(header.DescriptorOffset, out _))
        {
            return false;
        }

        var offsetInPayload = (int)header.DescriptorOffset - BlockHeader.Size;
        var place = layout.RootPayload(arena, root).Slice(offsetInPayload, ValueBlocks.DescriptorSize);
        var edge = ResidentDescriptor.Read(place);
        // Records can also use a Value block. Only Block is a leaf at a root edge.
        if (edge.Kind != DescriptorKindEnum.Block || ValueBlocks.AddressOf(edge) != source ||
            !RelocationCopy.TryPublish(arena, parts.Retired, source, sizeClass, region, out replacement, out var slotBytes))
        {
            return false;
        }

        // The old header still points to the current holder, so reclamation of
        // an older alias skips the old block before its raw retirement frees it.
        ResidentDescriptor.PublishLocation(place, replacement);
        parts.Retired.RetireRelocated(source, slotBytes);
        return true;
    }
}
