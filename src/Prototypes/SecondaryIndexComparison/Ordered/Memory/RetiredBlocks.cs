namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;

// What the writer retired and has not freed yet, reduced from the engine's RetiredMemory
// (src/Moltaro.Lattice.Engine/Engine/Protection/RetiredMemory.cs), itself adapted from the drain list of
// Tsavorite's LightEpoch (see ReadEpochs). A block is retired with the current epoch and freed by a
// later look, once no read that entered at or before that epoch is inside.
//
// When the writer looks: the engine's rule, modelled here. After a publication (AfterWrite), when the
// blocks retired since the last look reach the number of slots of the protection (ReadEpochs.SlotCount:
// 128 up to 64 logical processors) or their bytes reach one segment of the arena (4 MiB, the engine's
// LatticeDatabaseOptions.ArenaSegmentSize); and, once a look left something behind because a read held
// it, a publication that retired nothing counts as one block until a look leaves nothing behind. A look
// sets both counts to zero; what it could not free does not count toward the next one. A look is one
// move of the epoch (ReadEpochs.Advance), which every read's entry then loads anew: Looks counts them.
//
// One list may serve several structures, as the engine has one for the database: each structure
// retires into it, and the writer calls AfterWrite once per publication of a write, whatever number
// of structures that write changed.
//
// Room. A write reserves places before its publication (Reserve), so that retiring allocates nothing.
internal sealed class RetiredBlocks
{
    private readonly ReadEpochs _epochs;

    private readonly NodeArena _arena;

    private readonly Queue<(long Epoch, ulong Address)> _items = new();

    private int _itemsSinceLook;

    private long _bytesSinceLook;

    private int _retiredSinceWrite;

    private bool _leftBehind;

    public RetiredBlocks(ReadEpochs epochs, NodeArena arena)
    {
        _epochs = epochs;
        _arena = arena;
    }

    public int Count => _items.Count;

    // The moves of the epoch the looks made.
    public long Looks { get; private set; }

    // Every block retired, freed or not.
    public long Retired { get; private set; }

    // The most bytes the arena held retired at once.
    public long PeakRetiredBytes { get; private set; }

    public static long BytesPerLook => NodeArena.SegmentBytes;

    // Room for this many more retired blocks, before a publication.
    public void Reserve(int places) => _items.EnsureCapacity(_items.Count + places);

    public void Retire(ulong address)
    {
        _arena.Retire(address);
        _items.Enqueue((_epochs.Current, address));
        _itemsSinceLook++;
        _bytesSinceLook += _arena.SlotOf(address);
        _retiredSinceWrite++;
        Retired++;
        PeakRetiredBytes = Math.Max(PeakRetiredBytes, _arena.RetiredBytes);
    }

    // After the publication of a write (see the class).
    public void AfterWrite()
    {
        if (_leftBehind && _retiredSinceWrite == 0)
        {
            _itemsSinceLook++;
        }

        _retiredSinceWrite = 0;

        if (_itemsSinceLook >= _epochs.SlotCount || _bytesSinceLook >= BytesPerLook)
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

        Looks++;

        while (_items.TryPeek(out var item) && item.Epoch <= safe)
        {
            _items.Dequeue();
            _arena.Free(item.Address, BlockStateEnum.Retired);
        }

        _leftBehind = _items.Count > 0;
    }
}
