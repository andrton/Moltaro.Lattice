namespace Moltaro.Lattice.Schema;

// What a position holds, as read from a descriptor, before validation. A policy the
// descriptor does not state is null; the enum type is named, and resolved by SchemaBuilder.
internal readonly record struct ValueDeclarationDraft(
    LatticeFieldTypeEnum Type,
    bool IsNullable,
    LatticeStringComparisonEnum? Comparison,
    LatticeStringStorageEnum? StringStorage,
    string? EnumTypeName);
