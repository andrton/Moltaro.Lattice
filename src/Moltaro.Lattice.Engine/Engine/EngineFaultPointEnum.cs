namespace Moltaro.Lattice.Engine;

// The points of the path of a write at which a test of the engine can make the next write fail
// (EngineDatabase.ArmFault; exception X3, docs/work/exceptions.md). A point does nothing unless a
// test arms it and cannot be reached through the public API (brief of Epic 1, sprint S09, "Types and
// members of S09" and "Faulted").
//
// BeforePublication: once the write is prepared, before its publication begins.
//
// DuringPublication: inside the publication of the write, after some of its stores and before the
// rest. For a replace that is between the mark of the write in progress and the new stamp of its root
// entry, where a read of that entity retries (brief of S09, "A fault during publication and the
// reads that retry"). A failure here puts the instance into Faulted.
internal enum EngineFaultPointEnum
{
    BeforePublication = 1,
    DuringPublication = 2,
}
