using System.Buffers;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Replication.Load;

// The entries of a checked Map or Set. Inline and empty entries are already in the
// holder. Other entries use the same block placement as a List of the same layout.
internal struct ReceivedCollection(ValueDescriptor descriptor, ReadOnlySequence<byte> values) : ICollectionEntrySource
{
    public ReadOnlySequence<byte> Values { get; private set; } = values;

    public void Place(ref ValueHolder holder, in StoredField place, StructureLayout element, int offset, int count, bool allowInline)
    {
        if (!ValueBlocks.IsPending(descriptor))
        {
            return;
        }

        if (element.HoldsDescriptors)
        {
            var records = new ReceivedRecords(Values);
            NestedValues.PlaceRecords(ref holder, place, element, offset, count, ref records);
            Values = records.Values;
            return;
        }

        var length = (int)descriptor.Length;
        var bytes = ValueSource.Received(Values.Slice(0, length));
        Values = Values.Slice(length);
        holder.PlaceBytes(place, offset, length, ref bytes, allowInline);
    }
}
