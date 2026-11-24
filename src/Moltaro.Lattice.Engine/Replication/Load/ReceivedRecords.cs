using System.Buffers;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Replication.Load;

// The records of a List of records that a node received, as the wire carries them (BlockExport), for the one
// placement of such a List (Storage.Roots.IRecordSource, NestedValues.PlaceRecords): the values of a root
// stand at the payload of the first block of its records, and each block is followed by the values its
// records name. BlockImport.Check found it all valid, so the length of a block is that of its records and of
// the values inline their descriptors give, and a block is copied as it came, its descriptors pending (see
// BlockImport) until its records' values are placed in their order, a List of records among them through
// the same placement. The values of the root not placed yet are held here while the List is placed, and the
// caller takes them back where the List ends (Values).
internal struct ReceivedRecords : IRecordSource
{
    private ReadOnlySequence<byte> _values;

    // The records of a List whose first block begins the values given.
    public ReceivedRecords(ReadOnlySequence<byte> values)
    {
        _values = values;
    }

    // The values of the root after what the List placed.
    public readonly ReadOnlySequence<byte> Values => _values;

    public readonly int BlockLength(StructureLayout element, int first, int count)
    {
        var block = _values;
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
            for (var index = 0; index < element.DescriptorCount; index++)
            {
                var place = block.Slice((record * element.Size) + element.DescriptorField(index).Offset, ValueBlocks.DescriptorSize);

                BlockImport.LeavePending(place, ValueBlocks.Read(place));
            }
        }

        for (var record = 0; record < count && !records.Refused; record++)
        {
            BlockImport.PlaceValues(ref records, element, BlockHeader.Size + (record * element.Size), ref _values);
        }
    }
}
