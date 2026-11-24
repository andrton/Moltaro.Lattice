namespace Moltaro.Lattice.Storage.Values;

// The bytes of a packed List as a root stores them (PackedElements): the records of its elements, written as
// they are handed out, in their order, to fill the places the List goes to, its one block or its chunks one
// after the other, as ValueSource hands out the bytes of a Binary value. Fill fills each place to its last
// byte, whole records at a time. A record that does not fit the end of a place is written into the record of
// scratch: its first bytes end the place and the rest begins the next one, as a character of text is split,
// because a chunk boundary is not a boundary of the value (milestone contract, "Native layout and
// allocation"). A place is at least as long as the rest of a split record, since a record fits one chunk and
// the value is a whole number of records.
//
// The record of scratch has the stride of the records and belongs to the caller; a List that does not lie in
// chunks fills one place with all its records and needs none, so it may be empty then. As a ref struct the
// source lives on the stack of the write that places the List.
//
// A source of its own rather than a mode of ValueSource: as a mode, its fields grew ValueSource, and with it
// the frame of RootWriter.SetString, which holds two of them, from 240 to 320 bytes, all cleared on every
// call. Apart, ValueBlocks.TryStore over ValueSource keeps 983 bytes of code and a frame of 184 bytes (JIT
// listing of a Release build).
internal ref struct PackedSource : IValueSource
{
    private readonly PackedElements _packed;

    private readonly object _list;

    private readonly Span<byte> _record;

    // The index of the next element whose record is written.
    private int _next;

    // The bytes of the record of scratch that the last place did not take, at its end.
    private int _recordLeft;

    // The records of the elements of the List, in the form a record holds it.
    public PackedSource(PackedElements packed, object list, Span<byte> record)
    {
        _packed = packed;
        _list = list;
        _record = record;
    }

    // Fills the place with the rest of the record the last place split, the whole records that fit, and the
    // first bytes of the record after them.
    public void Fill(Span<byte> place)
    {
        var stride = _packed.Stride;
        var filled = 0;

        if (_recordLeft > 0)
        {
            _record[(stride - _recordLeft)..].CopyTo(place);
            filled = _recordLeft;
            _recordLeft = 0;
        }

        var whole = (place.Length - filled) / stride;

        _packed.Write(_list, _next, place.Slice(filled, whole * stride));
        _next += whole;
        filled += whole * stride;

        if (filled < place.Length)
        {
            var fits = place.Length - filled;

            _packed.Write(_list, _next++, _record);
            _record[..fits].CopyTo(place[filled..]);
            _recordLeft = stride - fits;
        }
    }
}
