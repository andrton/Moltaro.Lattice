namespace Moltaro.Lattice.Engine.Writes;

// The points of the path of a write at which a test of the engine can make the next write fail
// (EngineDatabase.ArmFault; exception X3, docs/work/exceptions.md). A point does nothing unless a
// test arms it and cannot be reached through the public API (brief of Epic 1, sprint S09, "Types and
// members of S09", "Faulted" and the answers of the planner to the contract author).
//
// Both points lie after the storage step of the group of the write, and a failure at either puts the
// instance into Faulted. An armed point throws InvalidOperationException with a message that names
// the point. A preparation that fails, releases everything and leaves the instance Ready needs no
// point: real failures of a preparation show it (a refusal of the arena, a cancelled write, a value
// the writer refuses).
internal enum EngineFaultPointEnum
{
    // The storage step of the group has completed and no store of the publication of the write has
    // been made. In MemoryOnly "committed" means that the storage step returned.
    BeforePublication = 1,

    // Inside the publication of the write. For a replace, after the store of the root and before the
    // new stamp of its root entry, the mark of the write in progress set; for a delete, after the
    // store of the state and before the new stamp, the mark set; for an insert, after the store of
    // the entry of the index and before the position is published.
    DuringPublication = 2,
}
