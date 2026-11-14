namespace Moltaro.Lattice.Schema;

// A field as read from a descriptor, before validation.
internal readonly record struct FieldDraft(int Id, string? Name, LatticeFieldTypeEnum Type, bool IsNullable);
