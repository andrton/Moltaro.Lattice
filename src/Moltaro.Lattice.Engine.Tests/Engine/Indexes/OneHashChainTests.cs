using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// Entries that all have one hash, given to the primary index directly (review round 1 of Epic 1,
// sprint S08, part 1: the index wrote past its table when many entries shared a hash). The engine
// mixes every bit of an integer or Guid key into its hash, so keys of one hash are no longer keys of
// an everyday structure: they have to be chosen for the mixing, by inverting it, which ties a test to
// the function the engine uses today. The chain is a matter of the index alone, so the test gives the
// index the hash it takes and states the chain whatever function makes the hashes. The main table
// grows by the number of entries and the chain of one hash by overflow alone (IndexTable, "The
// capacity policy"): every entry of the hash is found, an entry that is removed is not, wherever it
// lies in the chain, an enumeration of the table returns each entry once and no removed one, the
// emptied words of the chain are taken again by the entries added after, without more memory, and the
// index of entries of one hash takes at most twice that of as many entries of different hashes. That
// a key of the hash which was never added is absent is the comparison of the key the caller makes for
// every slot the chain gives (Entities.EntityTable); the chain gives exactly the slots added.
public sealed class OneHashChainTests
{
    private const uint OneHash = 0x5A5A_5A5A;

    [Theory]
    [InlineData(15)]
    [InlineData(70)]
    [InlineData(1_000)]
    public void Entries_of_one_hash_are_found_enumerated_once_and_removed_wherever_they_lie_in_the_chain(int count)
    {
        using var arena = Arena();
        var index = IndexOf(arena);

        for (var slot = 0u; slot < count; slot++)
        {
            index.Reserve(OneHash);
            index.Add(OneHash, slot);
        }

        Assert.Equal(Enumerable.Range(0, count).Select(slot => (uint)slot), Found(index, OneHash).Order());
        Assert.Equal(Enumerable.Range(0, count).Select(slot => (uint)slot), Enumerated(index).Order());
        Assert.Empty(Found(index, OneHash + 1));

        var bytes = index.Bytes;

        // Every other entry first, then the rest: removed entries lie in the main bucket and in every
        // overflow bucket of the chain.
        for (var slot = 0u; slot < count; slot += 2)
        {
            index.Remove(OneHash, slot);
        }

        Assert.Equal(Enumerable.Range(0, count).Where(slot => slot % 2 == 1).Select(slot => (uint)slot), Found(index, OneHash).Order());
        Assert.Equal(Enumerable.Range(0, count).Where(slot => slot % 2 == 1).Select(slot => (uint)slot), Enumerated(index).Order());

        for (var slot = 1u; slot < count; slot += 2)
        {
            index.Remove(OneHash, slot);
        }

        Assert.Empty(Found(index, OneHash));
        Assert.Empty(Enumerated(index));

        // The emptied words are taken again, and the index takes no more memory than before.
        for (var slot = 0u; slot < count; slot++)
        {
            index.Reserve(OneHash);
            index.Add(OneHash, slot);
        }

        Assert.Equal(Enumerable.Range(0, count).Select(slot => (uint)slot), Found(index, OneHash).Order());
        Assert.Equal(bytes, index.Bytes);
    }

    [Fact]
    public void Index_of_entries_of_one_hash_takes_at_most_twice_that_of_as_many_entries_of_different_hashes()
    {
        const int Count = 3_000;
        using var arena = Arena();
        var oneHash = IndexOf(arena);
        var spread = IndexOf(arena);

        for (var slot = 0u; slot < Count; slot++)
        {
            oneHash.Reserve(OneHash);
            oneHash.Add(OneHash, slot);
            spread.Reserve(slot * 2_654_435_761u);
            spread.Add(slot * 2_654_435_761u, slot);
        }

        Assert.True(oneHash.Bytes <= 2 * spread.Bytes, $"The index of {Count} entries of one hash takes {oneHash.Bytes} bytes, more than twice the {spread.Bytes} of as many of different hashes.");
    }

    private static Arena Arena() => new(new ArenaOptions { MemoryLimit = new ArenaOptions().SegmentSize });

    private static PrimaryIndex IndexOf(Arena arena) =>
        new(new RetiredMemory(new ReadEpochs(new object()), arena, new RootTable()));

    private static List<uint> Found(PrimaryIndex index, uint hash)
    {
        var found = new List<uint>();
        var probe = index.Find(hash);

        while (probe.Next(out var slot))
        {
            found.Add(slot);
        }

        return found;
    }

    private static List<uint> Enumerated(PrimaryIndex index)
    {
        var table = index.TableAndVersion(out _);
        var enumerated = new List<uint>();
        var position = 0L;

        while (table.TryFindNext(ref position, out var slot))
        {
            enumerated.Add(slot);
        }

        return enumerated;
    }
}
