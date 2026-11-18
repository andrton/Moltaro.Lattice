using System.Diagnostics;
using System.Globalization;

namespace Moltaro.Lattice.Engine.Entities;

// One enumeration of the entities of one entity type, as it stands between two of its steps: where it
// is in the primary index, the keys it has returned (ReturnedKeys), the time its steps have spent
// inside the engine and the read limit that failed it, if one did; EntityTable.TryReadNext reads it
// and moves it on. It is managed memory of the enumeration alone: the engine holds no reference to it
// and it holds nothing of the engine, no table of the index, no root and no slot of the protection of
// reads, so an enumeration that is abandoned without being disposed leaves nothing behind, keeps
// nothing from being reused, and is counted nowhere (brief of S08, "Nothing is held between two
// steps").
//
// Where it is: the position of the next word of a table of the index to look at, and the version of
// that table (PrimaryIndex.TableAndVersion), which a growth changes. A position belongs to the table
// of its version, and a step that finds another table walks it from its start (EntityTable).
//
// The read limits of the options of the database (LatticeDatabaseOptions.ReadMemoryLimit and
// ReadTimeLimit), each null for no limit:
// - the memory: the bytes ReturnedKeys holds, by its rule, checked before it grows for the key of an
//   entry the pass has not returned, before the entry is copied;
// - the time: the time of the steps from their start to their end, added up over the pass, a wait
//   inside a step included, for a slot of the epoch table or for a write of an entity between its
//   stores; checked where a step can stop, each time a round of it has entered the protection of
//   reads, so once for every EntityTable.WordsPerRound words of the index it walks at the most, empty
//   ones included. The time between two steps is not counted, nor the wait for the position of a
//   session, which comes before the first step, nor the build of an entity, which runs the code of the
//   application outside the engine. It is counted in ticks of Stopwatch, and a limit longer than they
//   can count, such as TimeSpan.MaxValue, is the most they count, so nothing overflows.
// A pass without a limit pays nothing for the time, not even a timestamp, and nothing for the memory.
// A step that would exceed a limit fails with LatticeException, ReadLimitExceeded, and the limit; the
// pass lets its keys go, and every later step fails the same way, so it never ends as if it were
// complete.
internal sealed class EntityPass
{
    private readonly string _entityName;

    private readonly long? _memoryLimit;

    private readonly TimeSpan? _timeLimit;

    // The time limit in ticks of Stopwatch, and the ticks the steps have spent.
    private readonly long _timeTicks;

    private long _spentTicks;

    // The limit that failed the pass, null while none did.
    private LatticeLimitEnum? _failed;

    // Where the step that staged its entity leaves the pass once that entity is built (Commit).
    private long _stagedPosition;

    private int _stagedVersion;

    public EntityPass(string entityName, ReturnedKeys returned, long? memoryLimit, TimeSpan? timeLimit)
    {
        _entityName = entityName;
        Returned = returned;
        _memoryLimit = memoryLimit;
        _timeLimit = timeLimit;

        if (timeLimit is { } limit)
        {
            var ticks = (Int128)limit.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;

            _timeTicks = ticks > long.MaxValue ? long.MaxValue : (long)ticks;
        }
    }

    public ReturnedKeys Returned { get; }

    // The next word of the table of Version to look at. A new pass stands at the start of the first
    // table of the index, of version 0; if the index has grown since, its first step finds another
    // version and starts from the start all the same.
    public long Position { get; private set; }

    public int Version { get; private set; }

    public bool LimitsMemory => _memoryLimit is not null;

    // The start of a step, for its time: a timestamp, or 0 when there is no time limit.
    public long StartStep() => _timeLimit is null ? 0 : Stopwatch.GetTimestamp();

    // Fails the step when the pass failed before (see the class).
    public void ThrowIfFailed()
    {
        if (_failed is { } limit)
        {
            throw Exceeded(limit);
        }
    }

    // Fails the step that started at the timestamp when the steps, this one so far included, have
    // spent more than the time limit.
    public void CheckTime(long started)
    {
        if (_timeLimit is not null && _spentTicks + (Stopwatch.GetTimestamp() - started) > _timeTicks)
        {
            throw Fail(LatticeLimitEnum.ReadTime);
        }
    }

    // Fails the step when the keys would take more bytes than the memory limit.
    public void CheckMemory(long bytes)
    {
        if (bytes > _memoryLimit)
        {
            throw Fail(LatticeLimitEnum.ReadMemory);
        }
    }

    // Adds the time of the step that started at the timestamp.
    public void EndStep(long started)
    {
        if (_timeLimit is not null)
        {
            _spentTicks += Stopwatch.GetTimestamp() - started;
        }
    }

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

    private LatticeException Fail(LatticeLimitEnum limit)
    {
        _failed = limit;
        Returned.Clear();

        return Exceeded(limit);
    }

    private LatticeException Exceeded(LatticeLimitEnum limit)
    {
        var exceeded = limit == LatticeLimitEnum.ReadMemory
            ? string.Create(CultureInfo.InvariantCulture, $"The enumeration of {_entityName} would hold more than the {_memoryLimit:N0} bytes of {nameof(LatticeDatabaseOptions.ReadMemoryLimit)} for the keys it remembers.")
            : string.Create(CultureInfo.InvariantCulture, $"The enumeration of {_entityName} has spent more than the {_timeLimit} of {nameof(LatticeDatabaseOptions.ReadTimeLimit)} inside the engine.");

        return new LatticeException(LatticeErrorEnum.ReadLimitExceeded, $"{exceeded} The entities it returned stay valid, but it is not complete and returns no more; start a new enumeration.")
        {
            Limit = limit,
        };
    }
}
