using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Entities;

// Where one search of an entity table by the full key of one of its secondary indexes stands between two
// steps: the read path of an index, a step at a time (docs/design/ordered-index.md, "The search by a full
// key"). A value: the identity after the last entity the search returned, and the sequence at which a
// step saw the end of the run of the key, if one did; no node, no address and no slot of the protection.
// Its owner holds it with the key, bound to the fields of the index (Access.Sets.IndexCriteria), for the
// whole search: the one object of a lazy search (Sets.IndexMatches), or the stack of a search that runs to
// its end at once (Sets.SetIndex.FindAsync). A step gives the value that follows it, which the owner takes
// once it has built the entity, so that an entity whose build fails is read again by the next step.
//
// A step runs in the table, where the read protocol of its entities is (EntityTable.TrySearch): inside the
// protection of reads and without a lock it descends to the first entry of the key after the last identity
// returned (OrderedTree.TryFindFirst), takes the root entry the entry names by its stamp, as a read by key
// does, passes over an entry that is not live or whose root does not have the key (SecondaryIndex.HasKey),
// and copies the root; the owner builds the entity outside the protection. So every entity that has the
// key for the whole search is returned once, and none under a key its state does not have; a step
// allocates nothing but what the copy makes. An entity deleted and created again during a search has a new
// identity and can be returned twice (ordered-index.md, "How a range will be walked").
internal struct IndexSearch
{
    // The identity the next step starts from: after the last entity returned, default before the first.
    private RootHandle _from;

    // The sequence at which a step that returned an entity saw the end of the run of the key, null while
    // none did.
    private ulong? _endsAt;

    // Reads the next entity of the search through the index of the table, for the key bound to it, into
    // the copy (see the type): true when there is one, with the place of the search after it, which the
    // caller takes once it has built the entity; false at the end, with the sequence under which the index
    // showed no more entity of the key, taken as an enumeration takes the sequence of its end. The index is
    // null for an entity type the engine does not store: such a search finds nothing, as a read by key
    // does. ObjectDisposedException after the close. The token of an asynchronous search ends the wait for
    // a slot of the protection, with OperationCanceledException (Protection.ReadEpochs.Enter).
    public readonly bool TryReadNext<TCopy, TResult>(EntityTable table, SecondaryIndex? index, in IndexKey key, ref TCopy copy, out IndexSearch next, out ulong sequence, CancellationToken cancellationToken = default)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        next = this;

        return table.TrySearch<TCopy, TResult>(index, key, ref next._from, ref next._endsAt, ref copy, out sequence, cancellationToken);
    }
}
