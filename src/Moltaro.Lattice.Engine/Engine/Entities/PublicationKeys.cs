using System.Runtime.CompilerServices;
using Moltaro.Lattice.Storage.Budgeting;
using Moltaro.Lattice.Storage.Hashing;

namespace Moltaro.Lattice.Engine.Entities;

// The entities of one publication of several changes, a write batch or a transaction a child applies, as a
// set of (table, key): two are one entity when they are of the same entity type and their keys are one key as
// the schema compares them (PrimaryKeys.KeysEqual), so that two spellings of a String key compared without
// case are one entity, and the same key in another entity type is another one
// (docs/decisions/mutation-commands.md, "Repeated roots inside an explicit WriteBatch").
// - The set of a batch (OfBatch): a plain set, a working object of the batch under the estimate its members
//   already hold (docs/design/storage-and-maintenance.md, the memory budget). The write coordinator asks it
//   whether another write shares an entity with the batch (Writes.PendingWrite.SharesEntityWith), and the
//   receiver of a child's connection refuses a forwarded batch that names an entity twice by it
//   (Children.RequestReceiver). It is made before the batch is admitted, outside every lock, and not changed
//   afterwards, so any thread may read it.
// - The set of a child, an instance of this class: the keys the effects of the transaction being applied
//   name, so that it names each once (Child.Loading.ReceivedChanges). It stays between the transactions, so
//   it is kept in a ChargedSet, publication scratch charged to the budget of the child by the bytes of its
//   array. Only the thread that applies uses it, under the lock of the writes of the child, and empties it for
//   the next transaction (Clear).
internal sealed class PublicationKeys
{
    private readonly ChargedSet<(EntityTable Table, EntityKey Key)> _keys;

    // The set of a child with room for capacity entities, charged to the budget; BudgetRefusedException
    // when the budget cannot hold its array.
    public PublicationKeys(int capacity, MemoryBudget budget)
    {
        _keys = new ChargedSet<(EntityTable, EntityKey)>(Comparer.Instance, budget);
        _keys.EnsureCapacity(capacity);
    }

    public int Count => _keys.Count;

    // Adds the entity; false when the set holds it already.
    public bool TryAdd(EntityTable table, in EntityKey key) => _keys.Add((table, key));

    public bool Contains(EntityTable table, in EntityKey key) => _keys.Contains((table, key));

    // Empties the set and keeps its room.
    public void Clear() => _keys.Clear();

    // Empties the set and releases its room, for an owner that lets it go.
    public void Release() => _keys.Release();

    public void ReportHeld(List<HeldArray> held) => _keys.ReportHeld(held);

    // The set of the entities of a batch with room for capacity entities (see the class).
    public static HashSet<(EntityTable Table, EntityKey Key)> OfBatch(int capacity) => new(capacity, Comparer.Instance);

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
