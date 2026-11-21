namespace Moltaro.Lattice.Engine.Entities;

// The primary key of the first entity a lazy search by a full key returned without seeing its end, which
// the step that returned it keeps instead of a WalkProgress: the walk needs no set of the keys it returned
// until its next step, which makes the set and begins it with this key (EntityTable.TrySearch;
// docs/design/ordered-index.md, "How a range is walked"). A search that takes the first of many entities and
// stops so allocates this and nothing of a set.
//
// The key is taken in that step from the root it copied, inside the protection of reads, and stored once the
// step has left it, so nothing is read twice: it is the key of the entity returned, and the walk passes that
// entity over at every later step, also when it is deleted and created again ahead of the walk between the
// two steps. An integer or a Guid key, as the words of a place of the set hold it (ReturnedKeys), with its
// hash in the primary index. What a search remembers counts against the memory limit of a read from the
// moment it remembers it, so this counts its Bytes when it is kept, and beside the set while the next step
// makes the set (LatticeDatabaseOptions.ReadMemoryLimit).
//
// Where it is not kept, the walk makes its set at its first step, as any walk does:
// - for a String primary key, whose text would have to be copied out of the root inside the protection;
// - for FindAsync, which holds its answer and counts its first entity against the memory limit as it adds
//   it, with what the walk remembers;
// - for a search by leading parts and a range, which stands after the entry of its entity and keeps that
//   entry, detached, in the arrays of its WalkProgress.
//
// Immutable, and held only by the search that kept it.
internal sealed class FirstKey
{
    // What it counts against the memory limit: the object, as WalkAllocationTests counts it, 16 bytes that
    // every object has, the two words, 16, and the hash, padded to 8.
    public const long Bytes = 40;

    public FirstKey(uint hash, long first, long second)
    {
        Hash = hash;
        First = first;
        Second = second;
    }

    // The hash of the key in the primary index (PrimaryKeys.StoredHash).
    public uint Hash { get; }

    // The words of the key: the integer and 0, or the two halves of the Guid (PrimaryKeys.StoredWords).
    public long First { get; }

    public long Second { get; }
}
