using System.Buffers;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Replication.Load;

// Records of one collection on the wire (BlockExport), for NestedValues.PlaceRecords.
// Each record block is followed by the values its records name. BlockImport checked the
// payload, so each block is copied using the checked inline lengths. Descriptors are left
// pending until their values are placed in order, including nested collections. Values
// gives the caller the wire body after this collection.
internal struct ReceivedRecords : IRecordSource
{
    private ReadOnlySequence<byte> _values;

    // The first record block begins the supplied values.
    public ReceivedRecords(ReadOnlySequence<byte> values)
    {
        _values = values;
    }

    // The values after the records placed so far.
    public readonly ReadOnlySequence<byte> Values => _values;

    public readonly int BlockLength(StructureLayout element, int first, int count) =>
        BlockLength(element, count, _values);

    public static int BlockLength(StructureLayout element, int count, in ReadOnlySequence<byte> block)
    {
        var length = count * element.Size;
        Span<byte> descriptor = stackalloc byte[ValueBlocks.DescriptorSize];

        for (var record = 0; record < count; record++)
        {
            for (var index = 0; index < element.DescriptorCount; index++)
            {
                block.Slice((record * element.Size) + element.DescriptorField(index).Offset, ValueBlocks.DescriptorSize).CopyTo(descriptor);

                var received = ValueBlocks.Read(descriptor);

                if (received.Kind == DescriptorKindEnum.Inline)
                {
                    length += (int)received.Length;
                }
            }
        }

        return length;
    }

    public void Fill(ref ValueHolder records, StructureLayout element, int first, int count)
    {
        var block = records.Bytes;

        _values.Slice(0, block.Length).CopyTo(block);
        _values = _values.Slice(block.Length);

        // The block holds what it received: every value placed into it lies out of line, as it came.
        records.InlineEnd = records.End;

        for (var record = 0; record < count; record++)
        {
            BlockImport.PrepareDescriptors(element, block[(record * element.Size)..]);
        }

        for (var record = 0; record < count && !records.Refused; record++)
        {
            BlockImport.PlaceValues(ref records, element, BlockHeader.Size + (record * element.Size), ref _values);
        }
    }
}
