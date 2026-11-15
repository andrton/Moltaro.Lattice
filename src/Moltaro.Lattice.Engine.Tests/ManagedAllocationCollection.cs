namespace Moltaro.Lattice.Engine.Tests;

// The test collection of the tests that measure managed allocation with
// GC.GetAllocatedBytesForCurrentThread and assert that code allocates nothing. The counter belongs
// to the measuring thread, but a garbage collection that falls inside a measured window moves it
// by part of the thread's allocation buffer, although the measured code allocated nothing; the
// garbage collections that other tests cause by running in parallel did exactly that. So the tests
// of this test collection run alone, never in parallel with another test, and each runs inside a
// region in which the runtime starts no garbage collection at all
// (WithoutGarbageCollectionAttribute), so that no garbage collection, whichever thread causes it,
// the test runner's included, falls inside a window. A test class that measures managed allocation
// in a later sprint joins this test collection.
[CollectionDefinition(DisableParallelization = true)]
[WithoutGarbageCollection]
public sealed class ManagedAllocationCollection;
