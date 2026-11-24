using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// The single value of a packed Set entry is read and written as its declared CLR scalar, without
// boxing the members. A nullable entry keeps its bit and the zeroed value bytes under a null.
internal sealed class PackedSet<TValue> : PackedCollection
    where TValue : struct
{
    private readonly SequenceForm _form;

    private readonly int _valueOffset;

    private readonly byte _nullMask;

    public PackedSet(StructureLayout entry)
        : base(entry.Size)
    {
        _form = entry.Collection!.Sequence!;
        ref readonly var place = ref entry.Place(0);
        _valueOffset = place.Offset;
        _nullMask = place.IsNullable ? (byte)(1 << place.NullBit) : (byte)0;
    }

    public override int Count(object set) => _form.Count(set);

    public override IDisposable OpenEntries(object set)
    {
        if (_nullMask != 0)
        {
            return ((HashSet<TValue?>)set).GetEnumerator();
        }

        return ((HashSet<TValue>)set).GetEnumerator();
    }

    public override void Write(object cursor, int first, Span<byte> records)
    {
        if (_nullMask != 0)
        {
            WriteNullable((IEnumerator<TValue?>)cursor, records);
            return;
        }

        var entries = (IEnumerator<TValue>)cursor;
        for (var offset = 0; offset < records.Length; offset += Stride)
        {
            entries.MoveNext();
            StoredScalars.WriteValue(records.Slice(offset, Stride), entries.Current);
        }
    }

    private void WriteNullable(IEnumerator<TValue?> entries, Span<byte> records)
    {
        for (var offset = 0; offset < records.Length; offset += Stride)
        {
            entries.MoveNext();
            var record = records.Slice(offset, Stride);
            if (entries.Current is { } value)
            {
                record[0] = 0;
                StoredScalars.WriteValue(record[_valueOffset..], value);
            }
            else
            {
                record.Clear();
                record[0] = _nullMask;
            }
        }
    }

    public override object Create(int count) => _form.Create(count);

    public override void Read(object set, ReadOnlySpan<byte> records)
    {
        if (_nullMask != 0)
        {
            var nullable = (HashSet<TValue?>)set;
            for (var offset = 0; offset < records.Length; offset += Stride)
            {
                var record = records.Slice(offset, Stride);
                nullable.Add((record[0] & _nullMask) == 0 ? StoredScalars.ReadValue<TValue>(record[_valueOffset..]) : null);
            }

            return;
        }

        var values = (HashSet<TValue>)set;
        for (var offset = 0; offset < records.Length; offset += Stride)
        {
            values.Add(StoredScalars.ReadValue<TValue>(records.Slice(offset, Stride)));
        }
    }
}
