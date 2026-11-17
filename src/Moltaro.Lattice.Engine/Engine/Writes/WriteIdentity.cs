using Moltaro.Lattice.Engine.Entities;

namespace Moltaro.Lattice.Engine.Writes;

// What a write reserves at its admission (WriteCoordinator): its entity type and its key, as the schema
// compares keys, so that two spellings of a String key compared without case are one identity
// (EntityTable.KeysEqual) and have one hash (EntityTable.HashOf, seeded with the seed of the instance).
// A value, used as the key of a dictionary of the coordinator without boxing.
internal readonly struct WriteIdentity : IEquatable<WriteIdentity>
{
    private readonly EntityTable _table;

    private readonly EntityKey _key;

    public WriteIdentity(EntityTable table, in EntityKey key)
    {
        _table = table;
        _key = key;
    }

    public bool Equals(WriteIdentity other) => ReferenceEquals(_table, other._table) && _table.KeysEqual(_key, other._key);

    public override bool Equals(object? obj) => obj is WriteIdentity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_table.EntityType.Id, _table.HashOf(_key));
}
