using Moltaro.Lattice.Schema.Compilation;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Maintenance;

// Moves a live owned block under the counted writes lock. Backlinks identify
// a bounded path; each edge must still reach the block from its current root.
// A copied owner adopts validated children before its edge is published.
// Earlier readers keep the old bytes until raw FIFO reclamation
// (docs/design/arena-protocol.md, "Deferred reclamation").
internal static class CompoundRelocation
{
    public static bool TryMove(DatabaseParts parts, RootHandle expected, ArenaAddress source) =>
        TryMove(parts, expected, source, 0, out _);

    public static bool TryMove(DatabaseParts parts, RootHandle expected, ArenaAddress source, long region,
        out ArenaAddress replacement)
    {
        replacement = default;
        // Prepared master writes may carry the old address across the Writes
        // lock. The child keeps Writes through preparation and publication.
        if (parts.Coordinator.HoldsPreparedMemory() || expected.Slot >= (uint)parts.Roots.SlotExtent)
        {
            return false;
        }

        ref var entry = ref parts.Roots.EntryOfSlot(expected.Slot);
        var arena = parts.Arena;
        if (entry.Generation != expected.Generation || entry.State != RootStateEnum.Live || entry.Root == source ||
            !arena.TryFindRelocationDestination(source, ref region, out var sizeClass))
        {
            return false;
        }

        var layout = parts.Tables.Stored(entry.TypeId).Layout;
        if (!TryResolve(arena, layout, entry.Root, expected, source, out var holder) || !RelocationChildren.Validate(arena, holder))
        {
            return false;
        }

        var retired = parts.Retired;
        if (!RelocationCopy.TryPublish(arena, retired, source, sizeClass, region, out replacement, out var slotBytes))
        {
            return false;
        }

        RelocationChildren.Adopt(arena, holder, replacement);
        var header = holder.Header;
        if (header.Flags == BlockHeader.LookupSlotsFlag)
        {
            var index = ((int)header.DescriptorOffset - CollectionLookup.FirstChunkOffset) / sizeof(ulong);
            CollectionLookup.PublishChunk(CollectionLookup.Chunks(arena, header.Parent), index, replacement);
        }
        else
        {
            var edge = arena.Payload(header.Parent)[((int)header.DescriptorOffset - BlockHeader.Size)..];
            ResidentDescriptor.PublishLocation(edge, replacement);
        }

        retired.RetireRelocated(source, slotBytes);
        return true;
    }

    // At each admitted schema depth a collection can add a lookup, a directory
    // and a record block. Bound the parent path before reading any edge.
    private static bool TryResolve(Arena arena, RootLayout layout, ArenaAddress root, RootHandle expected, ArenaAddress source,
        out RelocationBlock block)
    {
        if (!RelocationBlock.TryReadRoot(arena, layout, root, expected, out block))
        {
            return false;
        }

        Span<ArenaAddress> path = stackalloc ArenaAddress[3 * (SchemaBuilder.MaximumDepth + 1)];
        var depth = 0;
        for (var at = source; at != root;)
        {
            if (depth == path.Length || !arena.TryReadLiveHeader(at, out var header) ||
                header.Root != expected || header.LayoutVersion != layout.LayoutVersion)
            {
                return false;
            }

            path[depth++] = at;
            at = header.Parent;
        }

        while (depth > 0)
        {
            var childAddress = path[--depth];
            var offset = arena.Header(childAddress).DescriptorOffset;
            if (!block.TryGetIndex(offset, out var index) ||
                !RelocationChildren.TryRead(arena, block, arena.Payload(block.Address), index, out var child) || child.Address != childAddress)
            {
                return false;
            }

            block = child;
        }

        return true;
    }
}
