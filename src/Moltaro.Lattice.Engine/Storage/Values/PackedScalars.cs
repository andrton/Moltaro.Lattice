using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage.Values;

// The packed elements of a List of a scalar that is not nullable, an Enum or a Reference by an integer or a
// Guid included (PackedElements): a record holds List<TValue>, and the record of an element is the value at
// the width of its type, which is the stride, as StoredScalars writes it.
internal sealed class PackedScalars<TValue> : PackedElements
    where TValue : struct
{
    public PackedScalars(int stride)
        : base(stride)
    {
    }

    public override int Count(object list) => ((List<TValue>)list).Count;

    public override void Write(object list, int first, Span<byte> records)
    {
        var elements = CollectionsMarshal.AsSpan((List<TValue>)list).Slice(first, records.Length / Stride);

        for (var index = 0; index < elements.Length; index++)
        {
            StoredScalars.WriteValue(records.Slice(index * Stride, Stride), elements[index]);
        }
    }

    public override object Create(int count) => new List<TValue>(count);

    public override void Read(object list, ReadOnlySpan<byte> records)
    {
        var elements = (List<TValue>)list;

        for (var offset = 0; offset < records.Length; offset += Stride)
        {
            elements.Add(StoredScalars.ReadValue<TValue>(records.Slice(offset, Stride)));
        }
    }
}
