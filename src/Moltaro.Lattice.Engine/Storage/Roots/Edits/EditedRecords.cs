using System.Buffers;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots.Edits;

// Supplies one block at a time to the shared record placement. Retained
// records copy their bitmap, scalars and inline bytes. A descriptor of an
// unchanged out-of-line value borrows the original edge.
internal readonly struct EditedRecords(
    Arena arena,
    LatticeStringEncodingEnum encoding,
    StoredCollection? source,
    IEnumerator<EditedEntry> entries,
    int perBlock) : IRecordSource, IDisposable
{
    private readonly EditedEntry[] _records = ArrayPool<EditedEntry>.Shared.Rent(perBlock);

    public int BlockLength(StructureLayout element, int first, int count)
    {
        var length = element.Size * count;
        for (var index = 0; index < count; index++)
        {
            entries.MoveNext();
            var entry = entries.Current;
            _records[index] = entry;
            if (entry.Original < 0)
            {
                length += NewInline(element, 0, entry.First);
                length += NewInline(element, 1, entry.Second);
                continue;
            }

            var (address, offset) = source!.Value.Record(entry.Original);
            var old = arena.Payload(address)[(offset - BlockHeader.Size)..];
            if (entry.ReplacesSecond)
            {
                ref readonly var key = ref element.Place(0);
                if (key.Type == StoredTypeEnum.String)
                {
                    length += Inline(ValueBlocks.Read(old[key.Offset..]));
                }

                length += NewInline(element, 1, entry.Second);
                continue;
            }

            for (var descriptor = 0; descriptor < element.DescriptorCount; descriptor++)
            {
                length += Inline(ValueBlocks.Read(old[element.DescriptorField(descriptor).Offset..]));
            }
        }

        return length;
    }

    public void Fill(ref ValueHolder holder, StructureLayout element, int first, int count)
    {
        holder.Bytes.Clear();
        for (var index = 0; index < count && !holder.Refused; index++)
        {
            var entry = _records[index];
            var at = BlockHeader.Size + index * element.Size;
            if (entry.Original < 0)
            {
                NestedValues.StartRecord(ref holder, element, at);
                Write(ref holder, element, at, 0, entry.First);
                if (!holder.Refused)
                {
                    Write(ref holder, element, at, 1, entry.Second);
                }

                continue;
            }

            var (address, offset) = source!.Value.Record(entry.Original);
            var old = arena.Payload(address)[(offset - BlockHeader.Size)..];
            if (entry.ReplacesSecond)
            {
                NestedValues.StartRecord(ref holder, element, at);
                ref readonly var key = ref element.Place(0);
                if (key.Type == StoredTypeEnum.String)
                {
                    holder.Retain(key, at + key.Offset, address, offset + key.Offset);
                }
                else
                {
                    old.Slice(key.Offset, key.Width).CopyTo(holder.At(at + key.Offset, key.Width));
                }

                if (!holder.Refused)
                {
                    Write(ref holder, element, at, 1, entry.Second);
                }

                continue;
            }

            old[..element.DescriptorOffset].CopyTo(holder.At(at, element.DescriptorOffset));
            for (var descriptor = 0; descriptor < element.DescriptorCount && !holder.Refused; descriptor++)
            {
                ref readonly var place = ref element.DescriptorField(descriptor);
                holder.Retain(place, at + place.Offset, address, offset + place.Offset);
            }
        }
    }

    public void Dispose() => ArrayPool<EditedEntry>.Shared.Return(_records, clearArray: true);

    private int NewInline(StructureLayout element, int position, object? value) =>
        value is not null ? NestedValues.InlineBytes(element, position, value, encoding) : 0;

    private static int Inline(in ValueDescriptor descriptor) => descriptor.Kind == DescriptorKindEnum.Inline ? (int)descriptor.Length : 0;

    private static void Write(ref ValueHolder holder, StructureLayout element, int at, int position, object? value)
    {
        if (value is not null)
        {
            NestedValues.Write(ref holder, element, at, position, value);
        }
    }
}
