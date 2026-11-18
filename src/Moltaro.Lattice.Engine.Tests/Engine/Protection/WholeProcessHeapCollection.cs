namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// The test collection of the tests that measure the managed memory the whole process holds after a full
// garbage collection (GC.GetTotalMemory), to state what an object graph of the engine keeps alive. They
// run alone, never in parallel with another test, so that no other test holds or lets go of memory
// between two measurements.
[CollectionDefinition(DisableParallelization = true)]
public sealed class WholeProcessHeapCollection;
