using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Maintenance;

// The shape of a live block established by the preceding edge of its owner
// path, under Writes. A Records count is inherited from the collection and
// chunk ordinal, because the payload includes both records and inline bytes.
internal readonly record struct RelocationBlock(
    ArenaAddress Address, BlockHeader Header, StructureLayout? Layout, int Length)
{
    private bool IsStructure => Header.Kind == BlockKindEnum.Root || Header.Flags == BlockHeader.RecordBlockFlag;

    public int EdgeCount
    {
        get
        {
            if (IsStructure)
            {
                return Layout!.DescriptorCount * Length;
            }

            return Header.Kind switch
            {
                BlockKindEnum.ChunkDirectory => Layout is null ? ValueBlocks.ChunkCount(Length) : RecordChunkCount(Length, Layout.RecordsPerChunk),
                BlockKindEnum.Lookup => 1 + CollectionLookup.ChunkCountForSlots(CollectionLookup.SlotsNeeded(Length)),
                _ => 0,
            };
        }
    }

    public static bool TryReadRoot(Arena arena, RootLayout layout, ArenaAddress root, RootHandle expected,
        out RelocationBlock block)
    {
        block = default;
        if (!arena.TryReadLiveHeader(root, out var header) || header.Kind != BlockKindEnum.Root ||
            !RelocationEdges.HasOwner(header, expected, default, 0, layout.LayoutVersion) ||
            header.PayloadLength < layout.Structure.Size)
        {
            return false;
        }

        block = new RelocationBlock(root, header, layout.Structure, 1);
        return true;
    }

    public static int RecordChunkCount(int count, int perChunk) => (count + perChunk - 1) / perChunk;

    public bool IsLookupWord(int index) => Header.Kind == BlockKindEnum.Lookup && index != 0;

    public int OffsetOf(int index)
    {
        if (Header.Kind == BlockKindEnum.Root)
        {
            return Layout!.DescriptorOffset + index * ValueBlocks.DescriptorSize;
        }

        if (Header.Flags == BlockHeader.RecordBlockFlag)
        {
            var layout = Layout!;
            var record = index / layout.DescriptorCount;
            var descriptor = index % layout.DescriptorCount;
            return BlockHeader.Size + record * layout.Size + layout.DescriptorOffset + descriptor * ValueBlocks.DescriptorSize;
        }

        return IsLookupWord(index)
            ? CollectionLookup.FirstChunkOffset + (index - 1) * sizeof(ulong)
            : BlockHeader.Size + index * ValueBlocks.DescriptorSize;
    }

    public bool TryGetIndex(uint offset, out int index)
    {
        index = -1;
        if (offset < BlockHeader.Size || offset >= BlockHeader.Size + Header.PayloadLength)
        {
            return false;
        }

        var at = (int)offset;
        if (Header.Kind == BlockKindEnum.Root)
        {
            index = AlignedIndex(at - Layout!.DescriptorOffset, ValueBlocks.DescriptorSize);
        }
        else if (Header.Flags == BlockHeader.RecordBlockFlag)
        {
            var layout = Layout!;
            var record = (at - BlockHeader.Size) / layout.Size;
            var start = BlockHeader.Size + record * layout.Size + layout.DescriptorOffset;
            var descriptor = AlignedIndex(at - start, ValueBlocks.DescriptorSize);
            if ((uint)descriptor < (uint)layout.DescriptorCount)
            {
                index = record * layout.DescriptorCount + descriptor;
            }
        }
        else if (Header.Kind == BlockKindEnum.Lookup)
        {
            if (at == CollectionLookup.EntriesOffset)
            {
                index = 0;
            }
            else
            {
                var word = AlignedIndex(at - CollectionLookup.FirstChunkOffset, sizeof(ulong));
                index = word >= 0 ? word + 1 : -1;
            }
        }
        else if (Header.Kind == BlockKindEnum.ChunkDirectory)
        {
            index = AlignedIndex(at - BlockHeader.Size, ValueBlocks.DescriptorSize);
        }

        return (uint)index < (uint)EdgeCount;
    }

    public StoredTypeEnum FieldType(int index) => Layout!.DescriptorField(index % Layout!.DescriptorCount).Type;

    public StructureLayout? Element(int index) =>
        Header.Kind == BlockKindEnum.Lookup ? Layout : Layout!.DescriptorElement(index % Layout!.DescriptorCount);

    public int InlineStart => BlockHeader.Size + Layout!.Size * Length;

    private static int AlignedIndex(int offset, int width) =>
        offset >= 0 && offset % width == 0 ? offset / width : -1;
}
