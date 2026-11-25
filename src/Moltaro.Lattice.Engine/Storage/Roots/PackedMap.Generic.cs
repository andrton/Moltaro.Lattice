using System.Collections;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// Reads and writes a packed Map's scalar pairs using their normalized record types. The layout
// gives the two scalar slices and the bit for a nullable value. With a null the value bytes are zero.
internal sealed class PackedMap<TKey, TValue> : PackedCollection
    where TKey : struct
    where TValue : struct
{
    private readonly MapForm _form;
    private readonly int _keyOffset;
    private readonly int _keyWidth;
    private readonly int _valueOffset;
    private readonly int _valueWidth;
    private readonly byte _nullMask;

    public PackedMap(StructureLayout entry, MapForm form)
        : base(entry.Size)
    {
        _form = form;
        ref readonly var key = ref entry.Place(0);
        ref readonly var value = ref entry.Place(1);
        _keyOffset = key.Offset;
        _keyWidth = key.Width;
        _valueOffset = value.Offset;
        _valueWidth = value.Width;
        _nullMask = value.IsNullable ? (byte)(1 << value.NullBit) : (byte)0;
    }

    public override int Count(object map) => ((IDictionary)map).Count;

    public override IDisposable OpenEntries(object map)
    {
        if (_nullMask != 0)
        {
            return ((IEnumerable<KeyValuePair<TKey, TValue?>>)map).GetEnumerator();
        }

        return ((IEnumerable<KeyValuePair<TKey, TValue>>)map).GetEnumerator();
    }

    public override void Write(object cursor, int first, Span<byte> records)
    {
        if (_nullMask != 0)
        {
            WriteNullable((IEnumerator<KeyValuePair<TKey, TValue?>>)cursor, records);
            return;
        }

        var entries = (IEnumerator<KeyValuePair<TKey, TValue>>)cursor;
        for (var offset = 0; offset < records.Length; offset += Stride)
        {
            entries.MoveNext();
            var (key, value) = entries.Current;
            var record = records.Slice(offset, Stride);
            StoredScalars.WriteValue(record.Slice(_keyOffset, _keyWidth), key);
            StoredScalars.WriteValue(record.Slice(_valueOffset, _valueWidth), value);
        }
    }

    private void WriteNullable(IEnumerator<KeyValuePair<TKey, TValue?>> entries, Span<byte> records)
    {
        for (var offset = 0; offset < records.Length; offset += Stride)
        {
            entries.MoveNext();
            var (key, value) = entries.Current;
            var record = records.Slice(offset, Stride);
            var valueBytes = record.Slice(_valueOffset, _valueWidth);
            StoredScalars.WriteValue(record.Slice(_keyOffset, _keyWidth), key);

            if (value is { } present)
            {
                record[0] = 0;
                StoredScalars.WriteValue(valueBytes, present);
            }
            else
            {
                record[0] = _nullMask;
                valueBytes.Clear();
            }
        }
    }

    public override object Create(int count) => _form.Create(count);

    public override void Read(object map, ReadOnlySpan<byte> records)
    {
        if (_nullMask != 0)
        {
            ReadNullable((Dictionary<TKey, TValue?>)map, records);
            return;
        }

        var result = (Dictionary<TKey, TValue>)map;
        for (var offset = 0; offset < records.Length; offset += Stride)
        {
            var record = records.Slice(offset, Stride);
            var key = StoredScalars.ReadValue<TKey>(record.Slice(_keyOffset, _keyWidth));
            var value = StoredScalars.ReadValue<TValue>(record.Slice(_valueOffset, _valueWidth));
            result.TryAdd(key, value);
        }
    }

    private void ReadNullable(Dictionary<TKey, TValue?> result, ReadOnlySpan<byte> records)
    {
        for (var offset = 0; offset < records.Length; offset += Stride)
        {
            var record = records.Slice(offset, Stride);
            var key = StoredScalars.ReadValue<TKey>(record.Slice(_keyOffset, _keyWidth));
            TValue? value = (record[0] & _nullMask) == 0
                ? StoredScalars.ReadValue<TValue>(record.Slice(_valueOffset, _valueWidth))
                : null;
            result.TryAdd(key, value);
        }
    }
}
