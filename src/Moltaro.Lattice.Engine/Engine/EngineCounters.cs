using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine;

// What the engine of one database holds, as one value taken at one moment, for the tests and
// later for diagnostics (S39). Later sprints add members. After a write that failed validation
// or was refused, nothing of it is left in any counter (brief of S06, the planner's answer to
// Q30).
//
// Arena: the counters of the arena of the database, as ArenaCounters describes them. When every
// write has returned and no read is in progress, PreparedBytes and RetiredBytes are 0, and the
// counters equal an independent tally of what the entities hold.
//
// PrimaryIndexBytes: what the engine holds for the primary index, in bytes.
//
// RootEntries: the count of the table of root entries, the slots that hold a lifetime
// (RootTable.Count). After the deletes have returned and no read is in progress, it holds no
// entry of a deleted entity (the planner's answer to Q32).
//
// PublishedSequence: the sequence of the last published position, 0 for a database without a
// write and 1 after the first. The public API shows positions only; a test of the engine reads
// the number here (the planner's answer to Q38).
internal readonly record struct EngineCounters(
    ArenaCounters Arena, long PrimaryIndexBytes, int RootEntries, long PublishedSequence);
