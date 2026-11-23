using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Engine.Entities;

// The entities of one publication of several changes, a write batch or a transaction a child applies, as a
// set of (table, key): two are one entity when they are of the same entity type and their keys are one key as
// the schema compares them (PrimaryKeys.KeysEqual), so that two spellings of a String key compared without
// case are one entity, and the same key in another entity type is another one
// (docs/decisions/mutation-commands.md, "Repeated roots inside an explicit WriteBatch").
// - The set of a batch: the write coordinator asks it whether another write shares an entity with the batch
//   (Writes.PendingWrite.SharesEntityWith). It is made before the batch is admitted, outside every lock, and
//   not changed afterwards, so any thread may read it.
// - The set of a child: the keys the effects of the transaction being applied name, so that it names each
//   once (Child.Loading.ReceivedChanges). Only the thread that applies uses it, under the lock of the writes of
//   the child, and empties it for the next transaction (Clear).
internal sealed class PublicationKeys
{
    private readonly HashSet<(EntityTable Table, EntityKey Key)> _keys;

    public PublicationKeys(int capacity)
    {
        _keys = new HashSet<(EntityTable, EntityKey)>(capacity, Comparer.Instance);
    }

    public int Count => _keys.Count;

    // Adds the entity; false when the set holds it already.
    public bool TryAdd(EntityTable table, in EntityKey key) => _keys.Add((table, key));

    public bool Contains(EntityTable table, in EntityKey key) => _keys.Contains((table, key));

    // Empties the set and keeps its room.
    public void Clear() => _keys.Clear();

    // Whether the two sets hold an entity in common, looked up in the larger for each entity of the smaller.
    public bool Overlaps(PublicationKeys other)
    {
        var (smaller, larger) = Count <= other.Count ? (this, other) : (other, this);

        return larger._keys.Overlaps(smaller._keys);
    }

    // The comparison of the entity type of the table, its hash the hash of the key in the primary index.
    private sealed class Comparer : IEqualityComparer<(EntityTable Table, EntityKey Key)>
    {
        public static readonly Comparer Instance = new();

        public bool Equals((EntityTable Table, EntityKey Key) left, (EntityTable Table, EntityKey Key) right) =>
            ReferenceEquals(left.Table, right.Table) && left.Table.Keys.KeysEqual(left.Key, right.Key);

        public int GetHashCode((EntityTable Table, EntityKey Key) entity) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(entity.Table), entity.Table.Keys.HashOf(entity.Key));
    }
}
