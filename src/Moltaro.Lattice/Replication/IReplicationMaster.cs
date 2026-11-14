namespace Moltaro.Lattice.Replication;

// What a child needs from its master.
internal interface IReplicationMaster
{
    // Gives the child the schema and the whole database and subscribes it to later
    // changes, as one step: the child misses nothing and receives nothing twice.
    // Returns false when the master no longer serves.
    bool TryAttach(IReplicationChild child);

    void Detach(IReplicationChild child);

    // Executes a write that a child forwards, after the position the write depends on.
    // Completes when the master has published the write; it does not wait for any child.
    // Failed conditions are LatticeException.
    ValueTask<WriteOutcome> ExecuteAsync(WriteRequest request, CancellationToken cancellationToken);
}
