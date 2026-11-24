using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage.Values;

// The packed elements of a List of a nullable scalar, an Enum or a Reference by an integer or a Guid included
// (PackedElements): a record holds List<TValue?>, and the record of an element is its null bitmap, the bit of
// the element set while the element is null, then the value at the width of its type, zero for a null, where
// the layout of the record places them (Roots.StructureLayout).
internal sealed class PackedNullableScalars<TValue> : PackedElements
    where TValue : struct
{
    // The byte of the bitmap that holds the null bit of the element, and the bit in it.
    private readonly int _nullByte;

    private readonly byte _nullMask;

    // Where the value begins in the record, after the bitmap.
    private readonly int _valueOffset;

    public PackedNullableScalars(int stride, int nullByte, byte nullMask, int valueOffset)
        : base(stride)
    {
        _nullByte = nullByte;
        _nullMask = nullMask;
        _valueOffset = valueOffset;
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
                record[.._valueOffset].Clear();
                StoredScalars.WriteValue(record[_valueOffset..], value);
            }
            else
            {
                record.Clear();
                record[_nullByte] = _nullMask;
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

            elements.Add((record[_nullByte] & _nullMask) != 0 ? null : StoredScalars.ReadValue<TValue>(record[_valueOffset..]));
        }
    }
}
