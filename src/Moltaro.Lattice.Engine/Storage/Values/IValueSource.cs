namespace Moltaro.Lattice.Storage.Values;

// What hands out the bytes of one value in their order, to fill the places the value goes to
// (ValueBlocks.TryStore): the bytes of a String or a Binary value (ValueSource), or the records of a packed
// List (PackedSource). A variation by the kind of the value alone, taken as the type argument of a ref
// struct, so that a String or a Binary is placed by code made for ValueSource: no virtual call, and nothing of
// a List in its frame.
internal interface IValueSource
{
    // Fills the place with the next bytes of the value, to its last byte.
    void Fill(Span<byte> place);
}
