using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Engine.Entities;

// The read limits of the options of a database (LatticeDatabaseOptions.ReadMemoryLimit and ReadTimeLimit,
// each null for no limit) and what one read has spent of them: the time its steps have spent inside the
// engine, added up over the read, and the limit that failed it, if one did. One enumeration (EntityPass)
// and one search through an index (IndexSearch) each hold one by value, so that both count and fail
// alike; a read without a limit pays nothing for the time, not even a timestamp, and nothing for the
// memory.
//
// What each limit counts is the holder's: the memory a read holds is given to FailureOfMemory by its holder,
// the keys an enumeration remembers or what a search remembers and, for FindAsync, the result it holds
// until it returns (docs/decisions/queries.md, Q07.1.1). The time is that of the steps from their start
// to their end, a wait inside a step included, checked where a step can stop safely; the time between two
// steps, the wait for the position of a session and the build of an entity are not counted. It is counted
// in ticks of Stopwatch, and a limit longer than they can count, such as TimeSpan.MaxValue, is the most
// they count, so nothing overflows.
//
// A step that would exceed a limit fails with LatticeException, ReadLimitExceeded, and the limit; the
// failure is kept, so that every later step fails the same way and the read never ends as if it were
// complete. A cancellation is reported before a limit: the two looks at the limits, that of a round when
// it enters the protection (FailureOfRound) and that at the memory (FailureOfMemory), take the token of
// the read and look at it first, so that no place of a step can look at a limit before the token. 24
// bytes: the options, the ticks spent and the limit that failed. Mutable, and only the thread that takes
// a step of its read changes it.
internal struct ReadLimits
{
    private readonly LatticeDatabaseOptions _options;

    // The ticks of Stopwatch the steps have spent.
    private long _spentTicks;

    // The limit that failed the read, null while none did.
    private LatticeLimitEnum? _failed;

    // The read limits of the options of the database, which every read takes.
    public ReadLimits(LatticeDatabaseOptions options) => _options = options;

    // Whether the options limit the memory of a read.
    public readonly bool LimitsMemory => _options.ReadMemoryLimit is not null;

    // The start of a step, for its time: a timestamp, or 0 when there is no time limit.
    public readonly long StartStep() => _options.ReadTimeLimit is null ? 0 : Stopwatch.GetTimestamp();

    // Adds the time of the step that started at the timestamp.
    // Inlined into the step of a search, which is compiled at full optimization at its first call and whose
    // inline budget left it out of line (JIT listing of EntityTable.Step).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndStep(long started)
    {
        if (_options.ReadTimeLimit is not null)
        {
            _spentTicks += Stopwatch.GetTimestamp() - started;
        }
    }

    // The look of a round of the step that started at the timestamp, once it has entered the protection of
    // reads, in this order: the token; a failure the read kept, which fails the step again; and the time
    // limit, which fails it when the steps, this one so far included, have spent more than it, and the
    // failure is kept. The names, of the entity type and of the index of a search, null for an enumeration,
    // go into the message, which is made only when the read fails. A cancellation is thrown here; the
    // failure of a limit is returned, for the holder to let go of what it remembers before it throws it,
    // and null when the round may go on.
    public LatticeException? FailureOfRound(long started, CancellationToken cancellationToken, string entityName, string? indexName)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_failed is { } failed)
        {
            return Exceeded(failed, entityName, indexName);
        }

        if (_options.ReadTimeLimit is { } limit && _spentTicks + (Stopwatch.GetTimestamp() - started) > TicksOf(limit))
        {
            _failed = LatticeLimitEnum.ReadTime;

            return Exceeded(LatticeLimitEnum.ReadTime, entityName, indexName);
        }

        return null;
    }

    // The look at the memory: the token, then whether the read would hold more bytes than the memory limit,
    // which fails the step; the failure is kept. As FailureOfRound, a cancellation is thrown here and the
    // failure of the limit is returned, null when the read may hold the bytes.
    public LatticeException? FailureOfMemory(long bytes, CancellationToken cancellationToken, string entityName, string? indexName)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (bytes > _options.ReadMemoryLimit)
        {
            _failed = LatticeLimitEnum.ReadMemory;

            return Exceeded(LatticeLimitEnum.ReadMemory, entityName, indexName);
        }

        return null;
    }

    // The time limit in ticks of Stopwatch, at most what they count.
    private static long TicksOf(TimeSpan limit)
    {
        var ticks = (Int128)limit.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;

        return ticks > long.MaxValue ? long.MaxValue : (long)ticks;
    }

    private readonly LatticeException Exceeded(LatticeLimitEnum limit, string entityName, string? indexName)
    {
        var read = indexName is null
            ? $"The enumeration of {entityName}"
            : $"The search of {entityName} through its index {indexName}";
        var exceeded = limit == LatticeLimitEnum.ReadMemory
            ? string.Create(CultureInfo.InvariantCulture, $"{read} would hold more than the {_options.ReadMemoryLimit:N0} bytes of {nameof(LatticeDatabaseOptions.ReadMemoryLimit)}.")
            : string.Create(CultureInfo.InvariantCulture, $"{read} has spent more than the {_options.ReadTimeLimit} of {nameof(LatticeDatabaseOptions.ReadTimeLimit)} inside the engine.");
        var rest = indexName is null
            ? "The entities it returned stay valid, but it is not complete and returns no more; start a new enumeration."
            : "The entities it returned stay valid, but it is not complete and returns no more; start a new search.";

        return new LatticeException(LatticeErrorEnum.ReadLimitExceeded, $"{exceeded} {rest}")
        {
            Limit = limit,
        };
    }
}
