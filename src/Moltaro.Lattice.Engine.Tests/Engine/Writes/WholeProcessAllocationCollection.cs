namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// The test collection of the tests that measure the managed memory the whole process allocates
// during a write (GC.GetTotalAllocatedBytes), because the write runs on threads of the engine as well
// as on the thread of the test. They run alone, never in parallel with another test, so that no other
// test allocates inside a measured window. The count is precise whether or not a garbage collection
// falls inside the window, so they need no region without garbage collection; and they could not
// have one: ManagedAllocationCollection gives its tests a budget of 64 MiB, which a value of 32 MiB
// with the copy a record keeps of it already fills.
[CollectionDefinition(DisableParallelization = true)]
public sealed class WholeProcessAllocationCollection;
