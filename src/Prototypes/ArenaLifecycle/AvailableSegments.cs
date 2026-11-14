using System.Numerics;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed class AvailableSegments
{
    // Fixed allocator metadata: changing availability never allocates a tree node
    // during publication. The allocator gate owns every access to this bitmap.
    private readonly ulong[] _words = new ulong[ArenaAllocator.MaximumSegments / 64];

    public int Count { get; private set; }

    public int Min
    {
        get
        {
            for (var index = 0; index < _words.Length; index++)
            {
                if (_words[index] != 0)
                {
                    return index * 64 + BitOperations.TrailingZeroCount(_words[index]);
                }
            }

            throw new InvalidOperationException("No available segment.");
        }
    }

    public void Add(int segment)
    {
        var mask = 1UL << (segment % 64);
        ref var word = ref _words[segment / 64];
        if ((word & mask) == 0)
        {
            word |= mask;
            Count++;
        }
    }

    public void Remove(int segment)
    {
        var mask = 1UL << (segment % 64);
        ref var word = ref _words[segment / 64];
        if ((word & mask) != 0)
        {
            word &= ~mask;
            Count--;
        }
    }
}
