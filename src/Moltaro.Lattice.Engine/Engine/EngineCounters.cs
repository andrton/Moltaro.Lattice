using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine;

// What the engine of one database holds, as one value taken at one moment, for the tests and
// later for diagnostics (S39). Later sprints add members.
//
// Arena: the counters of the arena of the database, as ArenaCounters describes them. A block is
// counted in exactly one of LiveBytes with ClassSlackBytes, PreparedBytes and RetiredBytes. When
// every write has returned and no read is in progress, PreparedBytes and RetiredBytes are 0, and
// the counters equal what the stored entities hold; a write that failed validation or was
// refused leaves nothing in them (brief of S06, the planner's answer to Q30).
//
// PrimaryIndexBytes: the bytes of the tables in which the primary index keeps its entries, for
// every entity type of the database, at the capacity the tables are allocated with, in use or
// not. The milestone contract charges them to the memory budget ("Index preparation and
// traversal").
internal readonly record struct EngineCounters(ArenaCounters Arena, long PrimaryIndexBytes);
