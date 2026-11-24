using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// The blocks that hold the records of a List of kind Records, in the order of its elements, each with the
// records it holds (DescriptorKindEnum.Records): the one block of a List of at most RecordsPerChunk elements,
// or the chunks its directory names, every chunk but the last holding RecordsPerChunk records. An empty List
// has no block. A directory entry that is still zero names no chunk and is passed over: the writer of a List
// leaves it so when the arena refused that chunk, and the walk over such a List frees the chunks it has
// (StructureWalk). The walk, the read, the export and the placement of a List go through its blocks so.
//
// A ref struct over the directory, for an explicit loop: it allocates nothing and reads the arena only, under
// what the caller holds that keeps the blocks of the root from being freed: the lock of the writes, or the
// protection of reads for a published root.
internal ref struct RecordBlocks
{
    private readonly Arena _arena;

    // The descriptor of the List, and the records one chunk holds.
    private readonly ValueDescriptor _records;

    private readonly int _perChunk;

    // The next chunk of the directory to look at; for a List in one block, 0 before it and 1 after.
    private int _next;

    // The blocks of the List whose descriptor is given, before the first; the element is the layout of its
    // records.
    public RecordBlocks(Arena arena, in ValueDescriptor records, StructureLayout element)
    {
        _arena = arena;
        _records = records;
        _perChunk = element.RecordsPerChunk;
    }

    // The block, its records lying from the first byte of its payload at the stride of the element.
    public ArenaAddress Block { get; private set; }

    // The place among the elements of the List of the first record of the block.
    public int First { get; private set; }

    // How many records the block holds.
    public int Count { get; private set; }

    // Moves to the next block that holds records; false after the last one.
    public bool MoveNext()
    {
        var count = (int)_records.Length;

        if (_records.Location == 0)
        {
            return false;
        }

        if (count <= _perChunk)
        {
            if (_next++ > 0)
            {
                return false;
            }

            Block = ValueBlocks.AddressOf(_records);
            First = 0;
            Count = count;

            return true;
        }

        var entries = _arena.Payload(ValueBlocks.AddressOf(_records));

        while (_next * _perChunk < count)
        {
            var chunk = _next++;
            var entry = ValueBlocks.Read(entries[(chunk * ValueBlocks.DescriptorSize)..]);

            if (entry.Location == 0)
            {
                continue;
            }

            Block = ValueBlocks.AddressOf(entry);
            First = chunk * _perChunk;
            Count = Math.Min(_perChunk, count - First);

            return true;
        }

        return false;
    }
}
