using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// What a test knows of the blocks it keeps, kept apart from the arena: the address of each block,
// the payload length the test asked for and a tag the test wrote into its header. From the
// lengths, by the definitions of the counters and with the size classes of the specification, it
// adds up the live bytes and the class slack the arena has to report.
internal sealed class ArenaTally
{
    private readonly List<(ArenaAddress Address, int PayloadLength, uint Tag)> _kept = [];

    private readonly HashSet<int> _sizeClasses = [];

    // The blocks the test keeps, in no particular order.
    public IReadOnlyList<(ArenaAddress Address, int PayloadLength, uint Tag)> Kept => _kept;

    // The header and the payload length of every kept block.
    public long LiveBytes { get; private set; }

    // The rest of the slots of the kept blocks: for each, its size class minus its header and
    // payload length.
    public long ClassSlackBytes { get; private set; }

    // The size class of every block allocated so far, kept or not.
    public IReadOnlySet<int> SizeClassesAllocated => _sizeClasses;

    public void Allocated(ArenaAddress address, int payloadLength, uint tag)
    {
        var sizeClass = SpecifiedSizeClasses.For(payloadLength);

        _kept.Add((address, payloadLength, tag));
        _sizeClasses.Add(sizeClass);
        LiveBytes += SpecifiedSizeClasses.HeaderSize + payloadLength;
        ClassSlackBytes += sizeClass - SpecifiedSizeClasses.HeaderSize - payloadLength;
    }

    // Forgets the kept block at this index and returns it, for the test to free it in the arena.
    public (ArenaAddress Address, int PayloadLength, uint Tag) Remove(int index)
    {
        var block = _kept[index];

        // The order of the kept blocks does not matter, so the last one takes the place of the
        // removed one.
        _kept[index] = _kept[^1];
        _kept.RemoveAt(_kept.Count - 1);

        LiveBytes -= SpecifiedSizeClasses.HeaderSize + block.PayloadLength;
        ClassSlackBytes -= SpecifiedSizeClasses.For(block.PayloadLength) - SpecifiedSizeClasses.HeaderSize - block.PayloadLength;

        return block;
    }
}
