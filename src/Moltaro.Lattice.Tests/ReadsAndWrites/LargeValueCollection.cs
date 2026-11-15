namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// The test collection of the tests that hold values of hundreds of megabytes. They run alone, after
// the tests that run in parallel, so that their memory is never added to the memory of other tests
// and the peak of the test process does not grow by what they hold. A test class that holds such
// values joins this test collection.
[CollectionDefinition(DisableParallelization = true)]
public sealed class LargeValueCollection;
