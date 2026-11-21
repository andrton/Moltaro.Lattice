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
// the entry it stands after, and the bytes of its answer. So a search that finds one entity or none and
// passes over no match holds the read limits and one reference more than a search did before the walk,
// and nothing else: 56 bytes, the identity and the end, 8 bytes each, the read limits, 24, the reference,
// 8, and the Offset, the direction and whether it holds its answer, 6, padded to 8.
//
// Its owner holds it with the key, bound to the fields of the index (Access.Sets.IndexCriteria), for the
// whole search: the one object of a lazy search (Sets.IndexMatches), or the stack of a search that runs to
// its end at once (Sets.SetIndex.FindAsync). A step gives the place past the entity it returns, which the
// owner takes with what the step staged once it has built the entity (Commit), so that an entity whose build
// fails is read again by the next step, while the time spent and a failure the step recorded stay
// (Sets.SetOperations.Next). A read limit that fails the search, and a step
// after the close or after the database faulted, make it let go of its WalkProgress (CheckRound,
// CheckMemory, EntityTable.TrySearch), and a lazy search lets go of it at its end and on Dispose. An entity
// whose build fails keeps it: the next step reads that entity again.
internal struct IndexSearch
{
    // The read limits of the options and what the search has spent of them, which the step checks and
    // moves where it lies.
    public ReadLimits Limits;

    // The search of a lazy Find: forward, skipping nothing, and holding nothing of its answer.
    public IndexSearch(LatticeDatabaseOptions options)
    {
        Limits = new ReadLimits(options);
    }

    // The search of FindAsync, in the direction given, which skips the matches of its Offset and holds its
    // answer until it returns.
    public IndexSearch(LatticeDatabaseOptions options, bool reverse, int offset)
    {
        Limits = new ReadLimits(options);
        Reverse = reverse;
        Offset = offset;
        HoldsAnswer = true;
    }

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

    // What only a walk of more than one entity needs; null until a step makes it.
    public WalkProgress? Progress { get; set; }

    // Where the search stands: the identity a search by a full key goes on from, and the sequence of an end
    // a step saw.
    public readonly (RootHandle From, ulong EndsAt) Place => (From, EndsAt);

    // The look of a round of the step that started at the timestamp (ReadLimits.CheckRound): the token, a
    // failure the search kept and the time limit. On a failure of a limit it lets go of what the search
    // remembers: a search that failed returns nothing more, and every later step fails at the failure the
    // read limits keep before it would read it.
    public void CheckRound(long started, CancellationToken cancellationToken, string entityName, string? indexName)
    {
        try
        {
            Limits.CheckRound(started, cancellationToken, entityName, indexName);
        }
        catch (LatticeException)
        {
            Progress = null;

            throw;
        }
    }

    // The look at the memory (ReadLimits.CheckMemory): the token, then whether the search would hold more
    // bytes than the memory limit; on a failure of the limit it lets go of what it remembers, as CheckRound
    // does. Every place a search looks at the memory limit looks through here.
    public void CheckMemory(long bytes, CancellationToken cancellationToken, string entityName, string? indexName)
    {
        try
        {
            Limits.CheckMemory(bytes, cancellationToken, entityName, indexName);
        }
        catch (LatticeException)
        {
            Progress = null;

            throw;
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
    // next step that returns an entity stages anew over it, before anything commits it.
    public void Commit((RootHandle From, ulong EndsAt) place)
    {
        (From, EndsAt) = place;
        Progress?.Commit();
    }
}
