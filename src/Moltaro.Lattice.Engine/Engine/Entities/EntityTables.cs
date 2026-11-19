using System.Diagnostics.CodeAnalysis;

namespace Moltaro.Lattice.Engine.Entities;

// The tables of the entity types of an open database, one for each entity type of its schema, made once
// when the database opens and never changed: in the order of the schema, and, for the entity types the
// engine stores, by the identity of their entity type, the form in which a root, a transaction and a
// forwarded write name them.
internal sealed class EntityTables
{
    private readonly Dictionary<uint, EntityTable> _stored;

    public EntityTables(EntityTable[] inSchemaOrder)
    {
        InSchemaOrder = inSchemaOrder;
        _stored = inSchemaOrder.Where(table => table.Layout is not null).ToDictionary(table => (uint)table.EntityType.Id);
    }

    // Every table, in the order of the entity types of the schema.
    public IReadOnlyList<EntityTable> InSchemaOrder { get; }

    // The table of the entity type with the identity, which the engine stores: a live root has one.
    public EntityTable Stored(uint typeId) => _stored[typeId];

    // The table of the entity type with the identity, when the engine stores it.
    public bool TryGetStored(uint typeId, [NotNullWhen(true)] out EntityTable? table) => _stored.TryGetValue(typeId, out table);
}
