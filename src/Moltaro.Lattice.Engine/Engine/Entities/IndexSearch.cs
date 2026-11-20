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
// A step, in its order. It enters the protection of reads, takes no lock and writes nothing but its slot
// of the protection:
// 1. It descends the tree of the index to the first entry of the key after the last identity returned
//    (OrderedTree.TryFindFirst), with the search key written on its stack (SearchKey). No entry: the
//    search ends.
// 2. It takes the root entry the identity names by its stamp, whatever its state, as a read by key
//    takes it (EntityTable, "A read by key", step 2): a write between its mark and its stamp is waited
//    for, so that a step that passes over a delete or a replace ends at its position or a later one. No
//    entry met keeps the mark for good: the entries of an insert enter the tree in the publication that
//    stores its root entry right after them (ChangePublisher.Publish), and the entries of a delete leave
//    the tree before its root entry is retired; a publication that fails between the two faults the
//    database, and the wait then reports Unavailable. An entry that is not live, a delete or an insert
//    whose state Live is not stored yet, is passed over: that insert has a position after the start of
//    the step.
// 3. The entry is a hint: unless the root has the key of the search (SecondaryIndex.HasKey), the step
//    passes over it to the next entry of the run; so an entity is never returned under a key its current
//    state does not have.
// 4. Otherwise it copies the root, as a read by key does (IRootCopy.Copy), remembers its identity, and
//    leaves the protection; the caller builds the entity outside it. When the entry after the one it
//    copied has another key and the sequence of the database did not change over the step, the search
//    has no further entity at that sequence: the next step ends there, as it would after its descent, and
//    takes no descent (EntityTable.TrySearch). It still enters the protection, so a step after the close
//    is ObjectDisposedException, also then.
// Every entity that has the key for the whole search is returned once: its entry is in the tree
// throughout, the entries of one key are ordered by identity, and each step starts after the last
// identity returned. A root entry that a search meets in a node is never released and given to another
// root while the search is inside, since the entries of a delete leave the tree before its entry is
// retired (Protection.RetiredMemory); so the identity needs no check of its generation.
//
// A step allocates nothing but what the copy makes. A search by the leading fields of an index, with a
// range, in reverse, or with Offset or Limit is the walk of ranges, which is not built. The step runs in
// the table, where the read protocol of its entities is (EntityTable.TrySearch); this value holds what
// lies between two steps, as the pass of an enumeration does (EntityPass).
//
// An entity that is deleted and created again during a search has a new root entry, and so a new
// identity, which the search can meet after the identity it returned before: such an entity can be
// returned twice. That every identity is returned once, also then, is a rule of the walk of ranges, which
// chooses how (docs/design/ordered-index.md, "How a range will be walked").
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
