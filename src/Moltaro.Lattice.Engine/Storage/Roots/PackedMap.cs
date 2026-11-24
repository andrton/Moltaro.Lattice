using System.Collections;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// The two members of a packed Map entry are written by the same layout as the fields of a root.
// The key and value are already in the record forms accepted by their declarations.
internal sealed class PackedMap : PackedCollection
{
    private readonly StructureLayout _entry;

    private readonly MapForm _form;

    public PackedMap(StructureLayout entry, MapForm form)
        : base(entry.Size)
    {
        _entry = entry;
        _form = form;
    }

    public override int Count(object map) => ((IDictionary)map).Count;

    public override IDisposable OpenEntries(object map) => _form.Entries(map).GetEnumerator();

    public override void Write(object cursor, int first, Span<byte> records)
    {
        var entries = (IEnumerator<KeyValuePair<object?, object?>>)cursor;
        for (var offset = 0; offset < records.Length; offset += Stride)
        {
            entries.MoveNext();
            var (key, value) = entries.Current;
            var holder = new ValueHolder(null!, null!, default, records.Slice(offset, Stride), 0, default, Stride);
            NestedValues.StartRecord(ref holder, _entry, at: 0);
            NestedValues.Write(ref holder, _entry, at: 0, position: 0, key!);
            if (value is not null)
            {
                NestedValues.Write(ref holder, _entry, at: 0, position: 1, value);
            }
        }
    }

    public override object Create(int count) => _form.Create(count);

    public override void Read(object map, ReadOnlySpan<byte> records)
    {
        for (var offset = 0; offset < records.Length; offset += Stride)
        {
            var record = records.Slice(offset, Stride);
            var key = NestedValues.Read(null!, _entry, record, default, position: 0, encoding: default);
            var value = NestedValues.Read(null!, _entry, record, default, position: 1, encoding: default);
            _form.TryAdd(map, key!, value);
        }
    }
}
