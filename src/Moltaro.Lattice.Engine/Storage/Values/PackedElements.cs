namespace Moltaro.Lattice.Storage.Values;

// How the elements of a packed List become its bytes and back: a List whose elements hold no descriptor lies
// as the bytes of a Binary value, one record of Stride bytes for each element, in their order
// (Roots.StructureLayout). It writes and reads spans of whole records, so that the caller divides the bytes
// among the places of the value as it likes, and a record that the boundary of a chunk divides goes through a
// record of scratch (PackedSource). One is made with the layout of the element when the
// schema is compiled, for the .NET type a record holds for the element, so that a List is written and read
// with no box for an element: a variation by that type alone (PackedScalars, PackedNullableScalars,
// Roots.PackedOwnedValues). It is called once for a span of records, never once for an element, and holds no
// state of its own.
internal abstract class PackedElements
{
    protected PackedElements(int stride) => Stride = stride;

    // The bytes of the record of one element.
    public int Stride { get; }

    // The number of elements of a List a record holds.
    public abstract int Count(object list);

    // Writes the records of the elements of the List from the one at the index first on, as many as the span
    // holds whole records, every byte of each.
    public abstract void Write(object list, int first, Span<byte> records);

    // A new List of the form a record holds, empty, with room for count elements.
    public abstract object Create(int count);

    // Adds to the List the elements of the whole records the span holds, in their order.
    public abstract void Read(object list, ReadOnlySpan<byte> records);
}
