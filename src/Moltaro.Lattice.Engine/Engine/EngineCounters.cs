using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine;

// What the engine of one database holds, as one value taken at one moment, for the tests and
// later for diagnostics (S39). Later sprints add members. After a write that failed validation
// or was refused, nothing of it is left in any counter (brief of S06, the planner's answer to
// Q30).
//
// Memory that a read could still reach when a write replaced or removed it is retired, not freed,
// and is freed once the reads that could reach it have left (arena protocol, "Deferred reclamation,
// 16 November 2026"); EngineDatabase.DrainRetired frees what no read in progress can reach.
//
// Arena: the counters of the arena of the database, as ArenaCounters describes them. When every
// write has returned, PreparedBytes is 0. RetiredBytes and RetiredIndexBytes count what a write
// replaced or removed while reads could still reach it; after a drain with no read in progress
// (EngineDatabase.DrainRetired) both are 0, and the counters equal an independent tally of what the
// entities hold.
//
// PrimaryIndexBytes: what the engine holds for the primary index, in bytes.
//
// RetiredIndexBytes: the replaced tables of the primary index that are not freed yet, in bytes
// counted as PrimaryIndexBytes counts a table.
//
// ReplacementIndexBytes: the table being built while the primary index grows, before it replaces
// the old one, in bytes counted in the same way. The one writer builds it within the insert that
// needs the room, and the counters are taken between two writes, so they read 0 here.
//
// RootEntries: the count of the table of root entries, the slots that hold a lifetime
// (RootTable.Count); the entry of a deleted root is counted until it is released. After the deletes
// have returned and a drain with no read in progress, it holds no entry of a deleted entity (the
// planner's answer to Q32, as amended on 16 November 2026).
//
// PublishedSequence: the sequence of the last published position, 0 for a database without a
// write and 1 after the first. The public API shows positions only; a test of the engine reads
// the number here (the planner's answer to Q38).
//
// The members of the write coordinator (brief of Epic 1, sprint S09, "Types and members of S09" and
// "What is counted"; Writes.WriteCoordinator). What waits is counted in the memory budget as the
// prepared capacity of the arena, Arena.PreparedBytes; there is no second figure of queued bytes. They
// are read beside the others without the lock of the coordinator, each one value of its own.
//
// QueuedWrites: a current number, the writes admitted and not yet completed: waiting for their
// identity, waiting to be prepared, being prepared, prepared, or in the commit stage. It is 0 when
// every call of a write has returned.
//
// LargestCommitGroup: the most writes one commit group held since the open, at most
// CommitGroup.MaxOperations.
//
// LargestCommitGroupBytes: the most bytes of PreparedWrite.EncodedSize one commit group held since the
// open, at most CommitGroup.MaxEncodedSize unless a larger write formed a group alone. The two maxima
// are taken independently of each other, and a lone large write is a group of one that counts in both.
//
// MemoryLimit: the memory budget in force, in bytes, fixed when the instance opened: the
// LatticeDatabaseOptions.MemoryLimit that was given, or half of
// GC.GetGCMemoryInfo().TotalAvailableMemoryBytes and never less than one segment when none was, so
// that a test can state the default. When a test gave the options of the arena (EngineTestInputs.Arena)
// they are the options of the arena, MemoryLimit of the options of the database is not read, and this
// is the limit of those options. It is the limit of the arena (brief of S09, "The refusal").
internal readonly record struct EngineCounters(
    ArenaCounters Arena,
    long PrimaryIndexBytes,
    long RetiredIndexBytes,
    long ReplacementIndexBytes,
    int RootEntries,
    long PublishedSequence,
    int QueuedWrites,
    int LargestCommitGroup,
    long LargestCommitGroupBytes,
    long MemoryLimit);
