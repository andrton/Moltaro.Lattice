namespace Moltaro.Lattice.Storage.Values;

// A packed Map or Set supplies its entries in one pass. PackedSource passes the cursor to Write
// for successive runs of whole records, including the record that crosses a chunk boundary.
internal abstract class PackedCollection : PackedElements
{
    protected PackedCollection(int stride)
        : base(stride)
    {
    }

    public abstract IDisposable OpenEntries(object collection);
}
