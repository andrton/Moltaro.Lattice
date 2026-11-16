namespace Moltaro.Lattice.TestSupport.Writes;

// The one reason for which the tests of Epic 1, sprint S09 are skipped until the engine has what they
// state: the write coordinator — admission by entity, preparation of several writes at once, the
// commit stage with its groups, cancellation up to the dispatch, the fault points and the state
// Faulted — and the memory budget of an instance (LatticeDatabaseOptions.MemoryLimit), which bounds
// the arena and names itself in a refusal. The tests of both test projects take it from here. A test
// that states what already holds is not skipped. The implementer of the sprint removes the skips and
// does not change what the tests assert.
internal static class NotImplemented
{
    public const string WriteCoordinator = "The write coordinator and the memory budget are not implemented yet in the engine.";
}
