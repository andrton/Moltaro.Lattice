using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Entities;

// Where one search of an entity table through one of its secondary indexes stands between two steps: the
// read path of an index, a step at a time (docs/design/ordered-index.md, "The search by a full key" and
// "How a range is walked"). Plain state that the step reads and moves (EntityTable.TrySearch): the
// identity after the last entity returned of a search by a full key, the sequence at which a step saw the
// end of the search, if one did, the read limits and what the search has spent of them (ReadLimits), its
// direction, the matches it still skips by Offset, and whether it holds its answer, as FindAsync does
// until it returns; no node, no address and no slot of the protection. What only a walk of more than one
// entity needs it holds in one object, made at the first step that returns an entity without seeing the
// end of the search or passes over a match by Offset (WalkProgress): the keys it returned or passed over,
// the entry it stands after, and the bytes of its answer. A lazy search by a full key of an integer or Guid
// key makes it at its second step, and its first step keeps only the key of the entity it returned
// (FirstKey), in the same reference. So a search that finds one entity or none and passes over no match
// holds 56 bytes and nothing else: the identity and the end, 8 bytes each, the read limits, 24, the
// reference, 8, and the Offset, the direction, whether it holds its answer, whether its key is full and
// whether it ends at the entity it returns, 8.
//
// A search by the full key of a unique index with no null part ends at the entity a step returns, whatever
// the fence of its leaf shows (EndsAtItsEntity, Commit): its value has at most one owner
// (docs/decisions/unique-indexes.md, "Non-null full-key equality has at most one matching root"), and a
// step after it could only meet a root that took the value between two steps, a second owner of one
// search.
//
// Its owner holds it with the key, bound to the fields of the index (Access.Sets.IndexCriteria), for the
// whole search: the one object of a lazy search (Sets.IndexMatches), or the stack of a search that runs to
// its end at once (Sets.SetIndex.FindAsync). A step gives the place past the entity it returns, which the
// owner takes with what the step staged once it has built the entity (Commit), so that an entity whose build
// fails is read again by the next step, while the time spent and a failure the step recorded stay
// (Sets.SetOperations.Next). A read limit that fails the search, and a step after the close or after the
// database faulted, make it let go of its WalkProgress (CheckRound, CheckMemory, EntityTable.TrySearch), and
// a lazy search lets go of it at its end and on Dispose. An entity whose build fails keeps it: the next step
// reads that entity again.
internal struct IndexSearch
{
    // The read limits of the options and what the search has spent of them, which the step checks and
    // moves where it lies.
    public ReadLimits Limits;

    // The search of a lazy Find through the index, null for an entity type the engine does not store, by the
    // key, a full key or not: forward, skipping nothing, and holding nothing of its answer.
    public IndexSearch(ReadSettings settings, SecondaryIndex? index, in IndexKey key)
    {
        Limits = new ReadLimits(settings);
        FullKey = index is not null && IndexWalk.IsFullKey(index.Layout, key);
        EndsAtItsEntity = FullKey && index!.IsUnique && !HasNull(key);
    }

    // The search of FindAsync through the index by the key, a full key or not, in the direction given, which
    // skips the matches of its Offset and holds its answer until it returns.
    public IndexSearch(ReadSettings settings, SecondaryIndex? index, in IndexKey key, bool reverse, int offset)
        : this(settings, index, key)
    {
        Reverse = reverse;
        Offset = offset;
        HoldsAnswer = true;
    }

    // Whether the key gives every part of the index and no range, a run of one key
    // (Indexes.IndexWalk.IsFullKey), decided once for the search: such a search stands at an identity in the
    // run (From), any other after the entry of the entity it returned last (WalkProgress.Last).
    public bool FullKey { get; }

    // Whether a step that returns an entity ends the search, decided once for the search: a search by the
    // full key of a unique index with no null part (see the struct), for every form that steps, the lazy
    // search, FindAsync with its Offset and its Limit, through a class and by name. A key with a null part,
    // a key of leading parts and a range end as any search does.
    public bool EndsAtItsEntity { get; }

    // For a search by a full key: in the index order, the identity the next step starts at, after the last
    // entity returned; in its reverse, the identity the next step stays before, that of the last entity
    // returned. Default before the first step, which starts at the start in either direction.
    public RootHandle From { get; set; }

    // The sequence at which a step that returned an entity saw the end of the search, 0 while none did: a
    // step that copies an entity has a sequence of 1 at least, the sequence of the write that made it.
    public ulong EndsAt { get; set; }

    // Whether the search walks the reverse of the index order.
    public bool Reverse { get; }

    // The matches the search still passes over before it returns one.
    public int Offset { get; set; }

    // Whether the search holds its answer until it returns, as FindAsync does, which counts against the
    // memory limit of a read once it has a WalkProgress.
    public bool HoldsAnswer { get; }

    // What the walk remembers between two steps, in the one reference the search holds for it: nothing; the
    // key a lazy search by a full key kept of its first entity, until its next step makes the set (FirstKey);
    // or what a walk of more than one entity needs (WalkProgress).
    private object? _remembered;

    // What only a walk of more than one entity needs; null until a step makes it.
    public WalkProgress? Progress
    {
        readonly get => _remembered as WalkProgress;
        set => _remembered = value;
    }

    // The key a lazy search by a full key kept of its first entity, until its next step makes the set; null
    // otherwise. Letting go of the progress lets go of it too.
    public readonly FirstKey? First => _remembered as FirstKey;

    // Where the search stands: the identity a search by a full key goes on from, the sequence of an end a step
    // saw, and no key kept of a first entity.
    public readonly (RootHandle From, ulong EndsAt, FirstKey? First) Place => (From, EndsAt, null);

    // The look of a round of the step that started at the timestamp (ReadLimits.FailureOfRound): the token, a
    // failure the search kept and the time limit. On a failure of a limit it lets go of what the search
    // remembers: a search that failed returns nothing more, and every later step fails at the failure the
    // read limits keep before it would read it.
    //
    // Not inlined. Inlined into the step of a search (EntityTable.Step) with the look of the read limits inside
    // it, it took the room the JIT gives to inlining there, and the look into the keys a lazy Find returned
    // (ReturnedKeys.Contains), made for every entity it meets, became a call, in the JIT listing of a Release
    // build. Called, it is the one call a round of the step makes.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void CheckRound(long started, CancellationToken cancellationToken, string entityName, string? indexName)
    {
        if (Limits.FailureOfRound(started, cancellationToken, entityName, indexName) is { } failure)
        {
            Progress = null;

            throw failure;
        }
    }

    // The refusal of a step of the search when the database refuses every read, after the close or a fault
    // (Protection.ReadEpochs.ThrowForState): the search is over for good and lets go of what it remembers,
    // as when a read limit fails it.
    [DoesNotReturn]
    public void RefuseForState(ReadEpochs epochs)
    {
        Progress = null;
        epochs.ThrowForState();
    }

    // The look at the memory (ReadLimits.FailureOfMemory): the token, then whether the search would hold more
    // bytes than the memory limit; on a failure of the limit it lets go of what it remembers, as CheckRound
    // does. Every place a search looks at the memory limit looks through here.
    public void CheckMemory(long bytes, CancellationToken cancellationToken, string entityName, string? indexName)
    {
        if (Limits.FailureOfMemory(bytes, cancellationToken, entityName, indexName) is { } failure)
        {
            Progress = null;

            throw failure;
        }
    }

    // The entity the last step returned was built and is returned: the search stands at the place that step
    // gave, past the entity, and what only a walk needs takes what the step staged. A build that fails calls
    // nothing here, and the search stays before that entity: From and EndsAt are as they were before the step,
    // but for a round of the step that ended at the bound of its work, where a search by a full key stood at
    // the identity of the entry it ended before (EntityTable.StandAt); and the entry a search of any other
    // key stands at (WalkProgress.Last) may also have moved in such a round. Either lies before that entity,
    // after entries it returned none of and whose matches skipped by Offset it remembered, so the next step
    // still meets the entity and counts none of those twice. What the failed step staged stays until the
    // next step that returns an entity stages anew over it, before anything commits it; a key the failed step
    // kept of a first entity (FirstKey) comes only with the place, so it is not taken either. The sequence is
    // that of the answer of the step: a search that ends at its entity (EndsAtItsEntity) ends there, where
    // the entity held the value and no other root did.
    // Inlined into the step of a set (SetOperations.Next): the choice of the end takes it over the inline
    // budget of the JIT, which would leave one call more for every entity a search returns (JIT listing of
    // Next).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Commit((RootHandle From, ulong EndsAt, FirstKey? First) place, ulong sequence)
    {
        From = place.From;
        EndsAt = EndsAtItsEntity ? sequence : place.EndsAt;

        if (place.First is { } first)
        {
            _remembered = first;

            return;
        }

        Progress?.Commit();
    }

    // Whether a value of the key is null.
    private static bool HasNull(in IndexKey key)
    {
        for (var place = 0; place < key.Count; place++)
        {
            if (key.ValueAt(place).IsNull)
            {
                return true;
            }
        }

        return false;
    }
}
