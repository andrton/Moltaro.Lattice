namespace Moltaro.Lattice.Engine.Entities;

// The read limits of the options of a database (LatticeDatabaseOptions.ReadMemoryLimit and ReadTimeLimit,
// each null for no limit) and what one read has spent of them: the time its steps have spent inside the
// engine, added up over the read, and the limit that failed it, if one did. One enumeration (EntityPass)
// and one search through an index (IndexSearch) each hold one by value, so that both count and fail
// alike; a read without a limit pays nothing for the time, not even a timestamp, and nothing for the
// memory.
//
// What each limit counts is the holder's: the memory a read holds is given to CheckMemory by its holder,
// the keys an enumeration remembers or what a search remembers and, for FindAsync, the result it holds
// until it returns (docs/decisions/queries.md, Q07.1.1). The time is that of the steps from their start
// to their end, a wait inside a step included, checked where a step can stop safely; the time between two
// steps, the wait for the position of a session and the build of an entity are not counted.
//
// A step that would exceed a limit fails with LatticeException, ReadLimitExceeded, and the limit; the
// failure is kept, so that every later step fails the same way and the read never ends as if it were
// complete. 24 bytes: the options, the ticks spent and the limit that failed. Mutable, and only the
// thread that takes a step of its read changes it.
internal struct ReadLimits
{
    // The read limits of the options of the database, which every read takes.
    public ReadLimits(LatticeDatabaseOptions options) =>
        throw new NotSupportedException("The read limits of a search are not implemented yet.");

    // Whether the options limit the memory of a read.
    public readonly bool LimitsMemory => throw new NotSupportedException("The read limits of a search are not implemented yet.");

    // The start of a step, for its time: a timestamp, or 0 when there is no time limit.
    public readonly long StartStep() => throw new NotSupportedException("The read limits of a search are not implemented yet.");

    // Adds the time of the step that started at the timestamp.
    public void EndStep(long started) => throw new NotSupportedException("The read limits of a search are not implemented yet.");

    // Fails the step when the read failed before; the names, of the entity type and of the index of a
    // search, null for an enumeration, go into the message, which is made only when the read fails.
    public readonly void ThrowIfFailed(string entityName, string? indexName) =>
        throw new NotSupportedException("The read limits of a search are not implemented yet.");

    // Fails the step that started at the timestamp when the steps, this one so far included, have spent
    // more than the time limit; the failure is kept.
    public void CheckTime(long started, string entityName, string? indexName) =>
        throw new NotSupportedException("The read limits of a search are not implemented yet.");

    // Fails the step when the read would hold more bytes than the memory limit; the failure is kept.
    public void CheckMemory(long bytes, string entityName, string? indexName) =>
        throw new NotSupportedException("The read limits of a search are not implemented yet.");
}
