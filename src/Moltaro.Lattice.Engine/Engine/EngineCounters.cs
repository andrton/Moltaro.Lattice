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
internal readonly record struct EngineCounters(
    ArenaCounters Arena,
    long PrimaryIndexBytes,
    long RetiredIndexBytes,
    long ReplacementIndexBytes,
    int RootEntries,
    long PublishedSequence);
