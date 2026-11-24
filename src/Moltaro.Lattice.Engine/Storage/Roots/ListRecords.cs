using System.Collections;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage.Roots;

// The records of a List that a write of values holds, in the form a record holds it, a List<T> of the elements
// (IRecordSource): each element written into its record by NestedValues.Write, with its values placed inline in
// the block when they are short enough and out of line otherwise. Its BlockLength adds up what those writes
// place inline (NestedValues.InlineBytes), so that the block is exactly the records and their values inline.
internal readonly struct ListRecords : IRecordSource
{
    private readonly IList _list;

    private readonly LatticeStringEncodingEnum _encoding;

    // The elements of the List, and the encoding of the text of the database.
    public ListRecords(IList list, LatticeStringEncodingEnum encoding)
    {
        _list = list;
        _encoding = encoding;
    }

    public int BlockLength(StructureLayout element, int first, int count)
    {
        var length = count * element.Size;

        for (var record = first; record < first + count; record++)
        {
            if (_list[record] is { } value)
            {
                length += NestedValues.InlineBytes(element, 0, value, _encoding);
            }
        }

        return length;
    }

    // The block is cleared first: after a refusal of the arena, the records the writing does not reach are
    // zero and hold only descriptors of kind Null, which name nothing to the free of the List.
    public void Fill(ref ValueHolder records, StructureLayout element, int first, int count)
    {
        records.Bytes.Clear();

        for (var record = 0; record < count && !records.Refused; record++)
        {
            var at = BlockHeader.Size + (record * element.Size);

            NestedValues.StartRecord(ref records, element, at);

            if (_list[first + record] is { } value)
            {
                NestedValues.Write(ref records, element, at, 0, value);
            }
        }
    }
}
