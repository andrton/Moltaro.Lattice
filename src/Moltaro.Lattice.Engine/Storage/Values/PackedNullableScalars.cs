using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage.Values;

// The packed elements of a List of a nullable scalar, an Enum or a Reference by an integer or a Guid included
// (PackedElements): a record holds List<TValue?>, and the record of an element is a bitmap of one byte, its
// lowest bit set while the element is null, then the value at the width of its type, zero for a null.
internal sealed class PackedNullableScalars<TValue> : PackedElements
    where TValue : struct
{
    private const int ValueOffset = 1;

    public PackedNullableScalars(int stride)
        : base(stride)
    {
    }

    public override int Count(object list) => ((List<TValue?>)list).Count;

    public override void Write(object list, int first, Span<byte> records)
    {
        var elements = CollectionsMarshal.AsSpan((List<TValue?>)list).Slice(first, records.Length / Stride);

        for (var index = 0; index < elements.Length; index++)
        {
            var record = records.Slice(index * Stride, Stride);

            if (elements[index] is { } value)
            {
                record[0] = 0;
                StoredScalars.WriteValue(record[ValueOffset..], value);
            }
            else
            {
                record.Clear();
                record[0] = 1;
            }
        }
    }

    public override object Create(int count) => new List<TValue?>(count);

    public override void Read(object list, ReadOnlySpan<byte> records)
    {
        var elements = (List<TValue?>)list;

        for (var offset = 0; offset < records.Length; offset += Stride)
        {
            var record = records.Slice(offset, Stride);

            elements.Add((record[0] & 1) != 0 ? null : StoredScalars.ReadValue<TValue>(record[ValueOffset..]));
        }
    }
}
