namespace Moltaro.Lattice.Schema;

// An index as read from a descriptor, before validation: its identity, its name, the ids of
// its fields and whether it is declared unique.
internal readonly record struct IndexDraft(int Id, string? Name, IReadOnlyList<int> FieldIds, bool IsUnique);
