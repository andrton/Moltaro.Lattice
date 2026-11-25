using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// The blocks of a lookup, chunks before their owner. Zero addresses are chunks a refused
// placement never took. The caller protects the lookup until the enumeration ends.
internal ref struct LookupBlocks
{
    private readonly ArenaAddress _owner;

    private readonly ReadOnlySpan<ArenaAddress> _chunks;

    private int _next;

    public LookupBlocks(Arena arena, in ValueDescriptor descriptor)
    {
        _owner = ValueBlocks.AddressOf(descriptor);
        if (CollectionLookup.IsChunked(descriptor))
        {
            _chunks = CollectionLookup.Chunks(arena, _owner);
        }
    }

    public ArenaAddress Current { get; private set; }

    public readonly LookupBlocks GetEnumerator() => this;

    public bool MoveNext()
    {
        while (_next < _chunks.Length)
        {
            var address = CollectionLookup.ReadChunk(_chunks, _next++);
            if (!address.IsNull)
            {
                Current = address;
                return true;
            }
        }

        if (_next++ == _chunks.Length)
        {
            Current = _owner;
            return true;
        }

        return false;
    }
}
