using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Placeholder.Replication;

// One confirmed change on its way from the master to a child: the new state of an entity,
// or none when the entity was deleted, and the position that includes the change.
internal readonly record struct ReplicatedChange(string EntityName, object Key, StoredEntity? Stored, ulong Sequence);
