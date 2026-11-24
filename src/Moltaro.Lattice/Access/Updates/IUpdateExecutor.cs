namespace Moltaro.Lattice.Access.Updates;

// The point where the core hands a single update to the set that executes it.
internal interface IUpdateExecutor
{
    ValueTask<LatticeWriteResult> ExecuteUpdateAsync(object key, UpdateInput update, LatticeRevision? expectedRevision,
        LatticeSession? session, CancellationToken cancellationToken);
}
