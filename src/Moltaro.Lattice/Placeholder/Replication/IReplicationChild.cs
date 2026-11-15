using Moltaro.Lattice.Placeholder.Storage;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Placeholder.Replication;

// What a master needs from each of its children.
internal interface IReplicationChild
{
    // Receives the schema, the initial state and the position that state corresponds to.
    // The child is readable afterwards. Returns false when the child no longer wants
    // them, because it was closed meanwhile.
    bool TryInstall(IReplicationMaster master, LatticeSchema schema, EntityStore initialState, LatticePosition position);

    // Accepts one confirmed change. The master calls this in the order of its writes and
    // does not wait: the child applies the changes in that order on its own.
    void Receive(ReplicatedChange change);

    // The master is gone. The child applies what it has received and keeps serving local
    // reads; writes become unavailable.
    void MasterClosed(IReplicationMaster master);
}
