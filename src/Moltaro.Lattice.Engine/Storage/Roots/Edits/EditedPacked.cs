using System.Buffers;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots.Edits;

// Copies the bytes of retained packed entries and writes new entries in the
// same form. A chunk boundary may split one record, whose remainder stays in
// scratch until the next fill.
internal ref struct EditedPacked(StructureLayout element, StoredCollection? stored, IEnumerator<EditedEntry> entries, Span<byte> split) : IValueSource
{
    private readonly Span<byte> _split = split;

    private int _remaining;

    public static void Place(ref ValueHolder holder, in StoredField place, StructureLayout element, int offset, int count,
        StoredCollection? stored, IEnumerator<EditedEntry> entries, bool allowInline)
    {
        var length = count * element.Size;
        if (length <= ValueBlocks.SingleBlockLimit)
        {
            var whole = new EditedPacked(element, stored, entries, default);
            holder.PlaceBytes(place, offset, length, ref whole, allowInline);
            return;
        }

        var budget = holder.Arena.Budget;
        var scratch = budget?.Allocate<byte>(element.Size, nameof(EditedPacked)) ?? ArrayPool<byte>.Shared.Rent(element.Size);
        try
        {
            var source = new EditedPacked(element, stored, entries, scratch.AsSpan(0, element.Size));
            holder.PlaceBytes(place, offset, length, ref source, allowInline);
        }
        finally
        {
            if (budget is not null)
            {
                budget.Release(scratch);
            }
            else
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }
    }

    public void Fill(Span<byte> place)
    {
        var stride = element.Size;
        if (_remaining != 0)
        {
            _split[(stride - _remaining)..].CopyTo(place);
            place = place[_remaining..];
            _remaining = 0;
        }

        while (place.Length >= stride)
        {
            WriteNext(place[..stride]);
            place = place[stride..];
        }

        if (!place.IsEmpty)
        {
            WriteNext(_split);
            _split[..place.Length].CopyTo(place);
            _remaining = stride - place.Length;
        }
    }

    private void WriteNext(Span<byte> bytes)
    {
        entries.MoveNext();
        var entry = entries.Current;
        if (entry.Original >= 0)
        {
            stored!.Value.CopyPacked(entry.Original, bytes);
            if (!entry.ReplacesSecond)
            {
                return;
            }
        }

        var holder = new ValueHolder(null!, null!, default, bytes, 0, default, bytes.Length);
        if (entry.ReplacesSecond)
        {
            ref readonly var key = ref element.Place(0);
            Span<byte> previous = stackalloc byte[16];
            bytes.Slice(key.Offset, key.Width).CopyTo(previous);
            NestedValues.StartRecord(ref holder, element, 0);
            previous[..key.Width].CopyTo(bytes[key.Offset..]);
        }
        else
        {
            NestedValues.StartRecord(ref holder, element, 0);
            if (entry.First is { } first)
            {
                NestedValues.Write(ref holder, element, 0, 0, first);
            }
        }

        if (entry.Second is { } second)
        {
            NestedValues.Write(ref holder, element, 0, 1, second);
        }
    }
}
