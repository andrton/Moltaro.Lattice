using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Maintenance;

// Shared blocks belong to a stable text ID. A root locator or one aligned
// chunk descriptor is the only edge to publish. An old directory can
// continue to refer to old chunks under its original native read epoch.
internal static class TextRelocation
{
    public static bool TryMove(DatabaseParts parts, RootHandle expected, ArenaAddress source, long region, out ArenaAddress destination)
    {
        destination = default;
        var arena = parts.Arena;
        if (expected.Generation != 0 || expected.Slot == 0 || parts.Coordinator.HoldsPreparedMemory() ||
            !arena.TryFindRelocationDestination(source, region, out var sizeClass) ||
            !arena.Texts.TryLocation(expected.Slot, out var current, out var length))
        {
            return false;
        }

        ref readonly var header = ref arena.Header(source);
        if (header.Root != expected || !header.Parent.IsNull)
        {
            return false;
        }

        if (header.Kind == BlockKindEnum.SharedTextChunk)
        {
            if (length <= ValueBlocks.SingleBlockLimit || !TryChunkEdge(arena, current, header, source, out var edge) ||
                !RelocationCopy.TryPublish(arena, parts.Retired, source, sizeClass, region, out destination, out var chunkBytes))
            {
                return false;
            }

            ResidentDescriptor.PublishLocation(edge, destination);
            parts.Retired.RetireRelocated(source, chunkBytes);
            return true;
        }

        var directory = length > ValueBlocks.SingleBlockLimit;
        if (source != current || header.DescriptorOffset != 0 ||
            header.Kind != (directory ? BlockKindEnum.SharedTextDirectory : BlockKindEnum.SharedText) ||
            header.PayloadLength != (directory ? (uint)(ValueBlocks.ChunkCount((int)length) * ValueBlocks.DescriptorSize) : length) ||
            !RelocationCopy.TryPublish(arena, parts.Retired, source, sizeClass, region, out destination, out var slotBytes))
        {
            return false;
        }

        arena.Texts.MoveAddress(expected.Slot, destination);
        parts.Retired.RetireRelocated(source, slotBytes);
        return true;
    }

    private static bool TryChunkEdge(Arena arena, ArenaAddress directory, scoped in BlockHeader chunk, ArenaAddress source, out Span<byte> edge)
    {
        edge = default;
        ref readonly var owner = ref arena.Header(directory);
        if (owner.Kind != BlockKindEnum.SharedTextDirectory || owner.Root != chunk.Root)
        {
            return false;
        }

        var offset = chunk.DescriptorOffset - BlockHeader.Size;
        if (offset >= owner.PayloadLength || owner.PayloadLength - offset < ValueBlocks.DescriptorSize || offset % ValueBlocks.DescriptorSize != 0)
        {
            return false;
        }

        edge = arena.Payload(directory).Slice((int)offset, ValueBlocks.DescriptorSize);
        var descriptor = ResidentDescriptor.ReadAligned(edge);
        return descriptor.Kind == DescriptorKindEnum.Block && descriptor.Length == chunk.PayloadLength &&
            ValueBlocks.AddressOf(descriptor) == source;
    }
}
