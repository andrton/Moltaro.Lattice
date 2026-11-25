using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Maintenance;

// Checks the physical edges used by relocation. All checks run under Writes
// before copying or repairing any edge (docs/design/arena-protocol.md).
internal static class RelocationEdges
{
    public static bool HasOwner(in BlockHeader header, RootHandle root, ArenaAddress parent, uint offset, uint layoutVersion) =>
        header.Root == root && header.Parent == parent && header.DescriptorOffset == offset && header.LayoutVersion == layoutVersion;

    public static bool IsRawDirectory(in BlockHeader header, uint bytes) =>
        header.Kind == BlockKindEnum.ChunkDirectory && bytes is > ValueBlocks.SingleBlockLimit and <= ValueBlocks.MostValueBytes &&
        header.PayloadLength == (uint)(ValueBlocks.ChunkCount((int)bytes) * ValueBlocks.DescriptorSize);
}
