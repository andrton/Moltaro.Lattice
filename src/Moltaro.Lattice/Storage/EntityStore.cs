using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage;

// The local data of one database instance: for every entity type, its entities by primary
// key. Placeholder for the native arena and its primary indexes.
// Not thread-safe: the owning database guards every call.
internal sealed class EntityStore
{
    private readonly Dictionary<string, Dictionary<object, StoredEntity>> _tables;

    public EntityStore(LatticeSchema schema)
        : this(schema.EntityTypes.ToDictionary(entityType => entityType.Name, _ => new Dictionary<object, StoredEntity>(), StringComparer.Ordinal))
    {
    }

    private EntityStore(Dictionary<string, Dictionary<object, StoredEntity>> tables)
    {
        _tables = tables;
    }

    public bool TryGet(string entityName, object key, out StoredEntity stored) =>
        _tables[entityName].TryGetValue(key, out stored);

    public StoredEntity[] GetAll(string entityName) => [.. _tables[entityName].Values];

    // Stores the new state of an entity, or removes the entity when there is none.
    public void Apply(string entityName, object key, StoredEntity? stored)
    {
        if (stored is { } value)
        {
            _tables[entityName][key] = value;
        }
        else
        {
            _tables[entityName].Remove(key);
        }
    }

    // A separate store with the same content, as the starting state of a child.
    public EntityStore Clone() =>
        new(_tables.ToDictionary(table => table.Key, table => new Dictionary<object, StoredEntity>(table.Value), StringComparer.Ordinal));
}
