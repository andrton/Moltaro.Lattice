using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Entities;

// Where one search of an entity table by the full key of one of its secondary indexes stands between two
// steps: the read path of an index, a step at a time (docs/design/ordered-index.md, "The search by a full
// key"). Plain state that the step reads and moves (EntityTable.TrySearch): the identity after the last
// entity the search returned, and the sequence at which a step saw the end of the run of the key, if one
// did; no node, no address and no slot of the protection. Its owner holds it with the key, bound to the
// fields of the index (Access.Sets.IndexCriteria), for the whole search: the one object of a lazy search
// (Sets.IndexMatches), or the stack of a search that runs to its end at once (Sets.SetIndex.FindAsync).
// The owner keeps the place of the search before a step and puts it back when it cannot build the entity
// the step returned (Sets.SetOperations.Next), so that the next step reads that entity again.
//
// A step runs in the table, where the read protocol of its entities is, inside the protection of reads and
// without a lock; the owner builds the entity outside the protection. So every entity that has the key for
// the whole search is returned once, and none under a key its state does not have; a step allocates
// nothing but what the copy makes. An entity deleted and created again during a search has a new identity
// and can be returned twice. ordered-index.md, "The search by a full key", says what a step does and why
// that holds.
internal struct IndexSearch
{
    // The walk of ranges, which is not built yet, stands here with the read limits of the options
    // (ReadLimits), in either direction, and with the matches it still skips by Offset; what only a walk
    // of more than one entity needs, it holds in one object, made at the first step that returns an
    // entity without seeing the end of the search (WalkProgress). The search of a lazy Find goes forward
    // and skips nothing.
    public IndexSearch(LatticeDatabaseOptions options, bool reverse, int offset) =>
        throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // The identity the next step starts from: after the last entity returned, default before the first.
    public RootHandle From { get; set; }

    // The sequence at which a step that returned an entity saw the end of the run of the key, null while
    // none did.
    public ulong? EndsAt { get; set; }

    // Whether the search walks the reverse of the index order.
    public readonly bool Reverse => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // Where the search stands, which its owner keeps before a step and puts back when it cannot build the
    // entity the step returned.
    public readonly (RootHandle From, ulong? EndsAt) Place => (From, EndsAt);

    // Puts the place of the search back to where it stood before a step.
    public void PutBack((RootHandle From, ulong? EndsAt) place) => (From, EndsAt) = place;

    // For FindAsync, until it returns: adds what its result holds, the capacity of its list and the stored
    // bytes of each entity, which counts against the memory limit of a read with what the search remembers.
    public void Hold(long bytes) => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // The entity the last step returned was built and is returned: what only a walk needs takes what that
    // step staged.
    public void Commit() => throw new NotSupportedException("The walk of ranges is not implemented yet.");
}
