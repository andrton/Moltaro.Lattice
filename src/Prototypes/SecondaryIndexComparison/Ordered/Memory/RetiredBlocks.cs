namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;

// What the writer of one structure retired and has not freed yet, reduced from the engine's
// RetiredMemory (src/Moltaro.Lattice.Engine/Engine/Protection/RetiredMemory.cs), itself adapted from the
// drain list of Tsavorite's LightEpoch (see ReadEpochs). A block is retired with the current epoch and
// freed by a later look, once no read that entered at or before that epoch is inside. The writer looks
// when the blocks retired since its last look reach the number of slots, or their bytes one segment of
// the arena, as the engine does; the second trigger of the engine (a look that left something behind)
// is left out.
//
// Room. A write reserves places before its publication (Reserve), so that retiring allocates nothing.
internal sealed class RetiredBlocks
{
    private readonly ReadEpochs _epochs;

    private readonly NodeArena _arena;

    private readonly Queue<(long Epoch, ulong Address)> _items = new();

    private int _itemsSinceLook;

    private long _bytesSinceLook;

    public RetiredBlocks(ReadEpochs epochs, NodeArena arena)
    {
        _epochs = epochs;
        _arena = arena;
    }

    public int Count => _items.Count;

    // Room for this many more retired blocks, before a publication.
    public void Reserve(int places) => _items.EnsureCapacity(_items.Count + places);

    public void Retire(ulong address)
    {
        _arena.Retire(address);
        _items.Enqueue((_epochs.Current, address));
        _itemsSinceLook++;
        _bytesSinceLook += _arena.SlotOf(address);
    }

    // After a publication.
    public void AfterWrite()
    {
        if (_itemsSinceLook >= _epochs.SlotCount || _bytesSinceLook >= NodeArena.SegmentBytes)
        {
            Drain();
        }
    }

    public void Drain()
    {
        _itemsSinceLook = 0;
        _bytesSinceLook = 0;

        if (_items.Count == 0)
        {
            return;
        }

        var safe = _epochs.Advance();

        while (_items.TryPeek(out var item) && item.Epoch <= safe)
        {
            _items.Dequeue();
            _arena.Free(item.Address, BlockStateEnum.Retired);
        }
    }
}
