namespace Moltaro.Lattice.TestSupport.Concurrency;

// The bound of a wait of a test: how long a test waits for what it expects to happen, a task to end, an
// event to be set, a peer to answer, before it fails. It is long enough that such a wait ends within it on
// a machine busy with other builds and suites, so that a wait that reaches it shows that the awaited thing
// did not happen, and short enough that a test that would hang fails instead of holding the run. It is no
// timeout of the database: a test that states one gives the database a value of its own.
internal static class TestWait
{
    public static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);
}
