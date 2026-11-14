namespace Moltaro.Lattice.Schema;

// A field as read from a descriptor, before validation: its identity, its name and what it
// holds.
internal readonly record struct FieldDraft(int Id, string? Name, ValueDeclarationDraft Declaration);
