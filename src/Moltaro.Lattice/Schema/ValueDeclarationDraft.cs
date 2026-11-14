namespace Moltaro.Lattice.Schema;

// What a position holds, as read from a descriptor, before validation. A policy the
// descriptor does not state is null; the enum type, the owned type and the entity type are
// named, and resolved by SchemaBuilder. Element, Key and Value are the positions inside a
// collection, read in the same way.
internal sealed record ValueDeclarationDraft(
    LatticeFieldTypeEnum Type,
    bool IsNullable,
    LatticeStringComparisonEnum? Comparison,
    LatticeStringStorageEnum? StringStorage,
    string? EnumTypeName,
    string? OwnedTypeName,
    string? EntityTypeName,
    ValueDeclarationDraft? Element,
    ValueDeclarationDraft? Key,
    ValueDeclarationDraft? Value);
