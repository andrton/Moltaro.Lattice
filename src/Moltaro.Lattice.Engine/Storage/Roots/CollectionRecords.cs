using System.Buffers;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage.Roots;

// Buffers only the entries of the next records block. Sizing and filling the block use the
// same entries, in input order, without revisiting the collection's enumerator.
internal readonly struct CollectionRecords : IRecordSource, IDisposable
{
    private readonly IEnumerator<KeyValuePair<object?, object?>> _entries;

    private readonly KeyValuePair<object?, object?>[] _block;

    private readonly LatticeStringEncodingEnum _encoding;

    public CollectionRecords(IEnumerator<KeyValuePair<object?, object?>> entries, int recordsPerChunk, LatticeStringEncodingEnum encoding)
    {
        _entries = entries;
        _block = ArrayPool<KeyValuePair<object?, object?>>.Shared.Rent(recordsPerChunk);
        _encoding = encoding;
    }

    public int BlockLength(StructureLayout element, int first, int count)
    {
        var length = count * element.Size;
        for (var index = 0; index < count; index++)
        {
            _entries.MoveNext();
            var entry = _entries.Current;
            _block[index] = entry;
            if (entry.Key is not null)
            {
                length += NestedValues.InlineBytes(element, 0, entry.Key, _encoding);
            }

            if (entry.Value is not null)
            {
                length += NestedValues.InlineBytes(element, 1, entry.Value, _encoding);
            }
        }

        return length;
    }

    public void Fill(ref ValueHolder records, StructureLayout element, int first, int count)
    {
        records.Bytes.Clear();
        for (var index = 0; index < count && !records.Refused; index++)
        {
            var at = BlockHeader.Size + index * element.Size;
            var (key, value) = _block[index];
            NestedValues.StartRecord(ref records, element, at);
            if (key is not null)
            {
                NestedValues.Write(ref records, element, at, position: 0, key);
            }

            if (value is not null)
            {
                NestedValues.Write(ref records, element, at, position: 1, value);
            }
        }
    }

    public void Dispose() => ArrayPool<KeyValuePair<object?, object?>>.Shared.Return(_block, clearArray: true);
}
