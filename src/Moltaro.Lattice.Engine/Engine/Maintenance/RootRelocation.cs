using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Maintenance;

// Under the writes lock of a counted maintenance step, moves a fixed root or
// a root whose direct native children are supported by this move. The copied
// root adopts those children before the root entry is release-published. Readers
// that already opened the old root keep it through their epochs. Its raw FIFO
// entry never follows its children (docs/design/arena-protocol.md).
internal static class RootRelocation
{
    public static bool TryMove(DatabaseParts parts, RootHandle expected, ArenaAddress source) =>
        TryMove(parts, expected, source, 0, allowOwnedChildren: true, out _);

    // The automatic turn currently selects and charges only fixed roots here.
    public static bool TryMove(DatabaseParts parts, RootHandle expected, ArenaAddress source, long region,
        out ArenaAddress replacement) =>
        TryMove(parts, expected, source, region, allowOwnedChildren: false, out replacement);

    private static bool TryMove(DatabaseParts parts, RootHandle expected, ArenaAddress source, long region,
        bool allowOwnedChildren, out ArenaAddress replacement)
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
        var hasDescriptors = layout.DescriptorCount != 0;
        var arena = parts.Arena;
        if ((hasDescriptors && !allowOwnedChildren) ||
            !arena.TryFindRelocationDestination(source, ref region, out var sizeClass) ||
            (hasDescriptors && !HasMovableChildren(arena, layout, expected, source)))
        {
            return false;
        }

        var retired = parts.Retired;
        if (!RelocationCopy.TryPublish(arena, retired, source, sizeClass, region, out replacement, out var slotBytes))
        {
            return false;
        }

        if (hasDescriptors)
        {
            foreach (var descriptor in new RootDescriptors(layout, arena.Payload(source)))
            {
                var child = RootStorage.OwnedAddress(descriptor);
                if (!child.IsNull)
                {
                    arena.Header(child).Parent = replacement;
                }
            }
        }

        // Publish the replacement before tagging the old slot. An epoch opened
        // after that tag can only reach the replacement or a later address.
        entry.ReleaseRoot(replacement);
        retired.RetireRelocated(source, slotBytes);
        return true;
    }

    private static bool HasMovableChildren(Arena arena, RootLayout layout, RootHandle expected, ArenaAddress root)
    {
        var payload = layout.RootPayload(arena, root);
        var descriptors = new RootDescriptors(layout, payload);
        var hasOwnedChild = false;
        while (descriptors.MoveNext())
        {
            var edge = descriptors.Current;
            if (edge.Kind is DescriptorKindEnum.Null or DescriptorKindEnum.Inline)
            {
                continue;
            }

            var child = RootStorage.OwnedAddress(edge);
            if (!arena.TryReadLiveHeader(child, out var header) ||
                !RelocationEdges.HasOwner(header, expected, root, (uint)descriptors.Offset, layout.LayoutVersion))
            {
                return false;
            }

            if (edge.Kind == DescriptorKindEnum.Block && header.Kind == BlockKindEnum.Value &&
                header.PayloadLength == edge.Length)
            {
                hasOwnedChild = true;
                continue;
            }

            if (edge.Kind != DescriptorKindEnum.Chunked || descriptors.Field.Type != StoredTypeEnum.Binary ||
                !RelocationEdges.IsRawDirectory(header, edge.Length))
            {
                return false;
            }

            hasOwnedChild = true;
        }

        return hasOwnedChild;
    }
}
