using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Maintenance;

// Moves a raw Binary directory or its short final chunk under the counted
// writes lock. An edge is repaired only when its owner is reachable from
// the current root. A copied directory adopts its children before publication.
// The old slot stays available to earlier readers until raw FIFO reclamation
// (docs/design/arena-protocol.md, "Deferred reclamation").
internal static class CompoundRelocation
{
    public static bool TryMove(DatabaseParts parts, RootHandle expected, ArenaAddress source)
    {
        // Prepared master writes may still carry the old address. A child
        // keeps the writes lock through preparation and publication.
        if (parts.Coordinator.HoldsPreparedMemory() || expected.Slot >= (uint)parts.Roots.SlotExtent)
        {
            return false;
        }

        ref var entry = ref parts.Roots.EntryOfSlot(expected.Slot);
        var arena = parts.Arena;
        var region = 0L;
        if (entry.Generation != expected.Generation || entry.State != RootStateEnum.Live ||
            !arena.TryFindRelocationDestination(source, ref region, out var sizeClass))
        {
            return false;
        }

        var sourceHeader = arena.Header(source);
        var layout = parts.Tables.Stored(entry.TypeId).Layout;
        if (sourceHeader.Kind is not (BlockKindEnum.ChunkDirectory or BlockKindEnum.Chunk) ||
            sourceHeader.Root != expected || sourceHeader.LayoutVersion != layout.LayoutVersion)
        {
            return false;
        }

        var movesDirectory = sourceHeader.Kind == BlockKindEnum.ChunkDirectory;
        var directory = movesDirectory ? source : sourceHeader.Parent;
        if (!arena.TryReadLiveHeader(directory, out var directoryHeader) ||
            directoryHeader.Parent != entry.Root || directoryHeader.Root != expected ||
            directoryHeader.LayoutVersion != layout.LayoutVersion ||
            !layout.TryGetDescriptorIndex(directoryHeader.DescriptorOffset, out var index) ||
            layout.DescriptorField(index).Type != StoredTypeEnum.Binary)
        {
            return false;
        }

        var rootOffset = (int)directoryHeader.DescriptorOffset - BlockHeader.Size;
        var rootEdge = layout.RootPayload(arena, entry.Root).Slice(rootOffset, ValueBlocks.DescriptorSize);
        var incoming = ResidentDescriptor.Read(rootEdge);
        if (incoming.Kind != DescriptorKindEnum.Chunked || ValueBlocks.AddressOf(incoming) != directory ||
            !RelocationEdges.IsRawDirectory(directoryHeader, incoming.Length))
        {
            return false;
        }

        var directoryBytes = arena.Payload(directory);
        var edge = rootEdge;
        if (movesDirectory)
        {
            if (!ValidateChildren(arena, directoryBytes, directory, expected, layout.LayoutVersion, (int)incoming.Length))
            {
                return false;
            }
        }
        else
        {
            // Only the short final chunk can leave a strictly sparse region.
            // Its immediate descriptor and the root edge above must both agree.
            var tailOffset = directoryBytes.Length - ValueBlocks.DescriptorSize;
            var tailLength = (int)incoming.Length - ((tailOffset / ValueBlocks.DescriptorSize) * ValueBlocks.ChunkPayload);
            edge = directoryBytes.Slice(tailOffset, ValueBlocks.DescriptorSize);
            var chunk = ResidentDescriptor.ReadAligned(edge);
            if (sourceHeader.DescriptorOffset != (uint)(BlockHeader.Size + tailOffset) ||
                sourceHeader.PayloadLength != (uint)tailLength || chunk.Kind != DescriptorKindEnum.Block ||
                chunk.Length != (uint)tailLength || ValueBlocks.AddressOf(chunk) != source)
            {
                return false;
            }
        }

        var retired = parts.Retired;
        if (!RelocationCopy.TryPublish(arena, retired, source, sizeClass, region, out var replacement, out var slotBytes))
        {
            return false;
        }

        if (movesDirectory)
        {
            for (var offset = 0; offset < directoryBytes.Length; offset += ValueBlocks.DescriptorSize)
            {
                var child = ValueBlocks.AddressOf(ResidentDescriptor.ReadAligned(directoryBytes[offset..]));
                arena.Header(child).Parent = replacement;
            }
        }

        ResidentDescriptor.PublishLocation(edge, replacement);
        retired.RetireRelocated(source, slotBytes);
        return true;
    }

    private static bool ValidateChildren(Arena arena, ReadOnlySpan<byte> directoryBytes, ArenaAddress directory,
        RootHandle owner, uint layoutVersion, int valueLength)
    {
        var remaining = valueLength;
        for (var offset = 0; offset < directoryBytes.Length; offset += ValueBlocks.DescriptorSize)
        {
            var expected = (uint)Math.Min(remaining, ValueBlocks.ChunkPayload);
            var chunk = ResidentDescriptor.ReadAligned(directoryBytes[offset..]);
            if (chunk.Kind != DescriptorKindEnum.Block || chunk.Length != expected ||
                !arena.TryReadLiveHeader(ValueBlocks.AddressOf(chunk), out var header) ||
                header.Kind != BlockKindEnum.Chunk || header.PayloadLength != expected ||
                !RelocationEdges.HasOwner(header, owner, directory, (uint)(BlockHeader.Size + offset), layoutVersion))
            {
                return false;
            }

            remaining -= (int)expected;
        }

        return true;
    }
}
