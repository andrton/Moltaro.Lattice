namespace Moltaro.Lattice.Engine.Entities;

// One enumeration of the entities of one entity type, as it stands between two of its steps: where it
// is in the primary index, the keys it has returned (ReturnedKeys), the time its steps have spent
// inside the engine and the read limit that failed it, if one did; EntityTable.TryReadNext reads it
// and moves it on. It is managed memory of the enumeration alone: the engine holds no reference to it
// and it holds nothing of the engine, no table of the index, no root and no slot of the protection of
// reads, so an enumeration that is abandoned without being disposed leaves nothing behind, keeps
// nothing from being reused, and is counted nowhere: nothing of the engine is held between two steps.
//
// Where it is: the position of the next word of a table of the index to look at, and the version of
// that table (PrimaryIndex.TableAndVersion), which a growth changes. A position belongs to the table
// of its version, and a step that finds another table walks it from its start (EntityTable).
//
// The read limits of the database, and what the pass has spent of them (ReadLimits): the
// memory is the bytes ReturnedKeys holds, by its rule, checked before it grows for the key of an entry the
// pass has not returned, before the entry is copied; the time is that of its steps, checked each time a
// round of a step has entered the protection of reads, so once for every EntityTable.WordsPerRound words of
// the index it walks at the most, empty ones included. A step that would exceed a limit fails with
// LatticeException, ReadLimitExceeded, and the limit; the pass lets its keys go, and every later step fails
// the same way, so it never ends as if it were complete.
internal sealed class EntityPass
{
    private readonly string _entityName;

    private ReadLimits _limits;

    // Where the step that staged its entity leaves the pass once that entity is built (Commit).
    private long _stagedPosition;

    private int _stagedVersion;

    public EntityPass(string entityName, ReturnedKeys returned, ReadSettings settings)
    {
        _entityName = entityName;
        Returned = returned;
        _limits = new ReadLimits(settings);
    }

    public ReturnedKeys Returned { get; }

    // The next word of the table of Version to look at. A new pass stands at the start of the first
    // table of the index, of version 0; if the index has grown since, its first step finds another
    // version and starts from the start all the same.
    public long Position { get; private set; }

    public int Version { get; private set; }

    public bool LimitsMemory => _limits.LimitsMemory;

    // The start of a step, for its time: a timestamp, or 0 when there is no time limit.
    public long StartStep() => _limits.StartStep();

    // The look of a round of the step that started at the timestamp (ReadLimits.FailureOfRound): the token, a
    // failure the pass kept and the time limit; on a failure of a limit the pass lets its keys go.
    public void CheckRound(long started, CancellationToken cancellationToken)
    {
        if (_limits.FailureOfRound(started, cancellationToken, _entityName, null) is { } failure)
        {
            Returned.Clear();

            throw failure;
        }
    }

    // The look at the memory (ReadLimits.FailureOfMemory): the token, then whether the keys would take more
    // bytes than the memory limit; on a failure of the limit the pass lets its keys go.
    public void CheckMemory(long bytes, CancellationToken cancellationToken)
    {
        if (_limits.FailureOfMemory(bytes, cancellationToken, _entityName, null) is { } failure)
        {
            Returned.Clear();

            throw failure;
        }
    }

    // Adds the time of the step that started at the timestamp.
    public void EndStep(long started) => _limits.EndStep(started);

    // A step found its entity at the word before the position, in the table of the version, and staged
    // its key.
    public void Stage(long position, int version)
    {
        _stagedPosition = position;
        _stagedVersion = version;
    }

    // The entity the last step staged was built and is returned: the pass moves past it and remembers
    // its key.
    public void Commit()
    {
        Position = _stagedPosition;
        Version = _stagedVersion;
        Returned.Commit();
    }
}
