namespace Moltaro.Lattice.Schema.Compilation;

// An index as read from a descriptor, before validation: its identity, its name, its parts,
// each the path of field ids from a field of the entity type to the indexed field, and
// whether it is declared unique. A part declared as one id is a path of that id alone.
internal readonly record struct IndexDraft(int Id, string? Name, IReadOnlyList<IReadOnlyList<int>> Parts, bool IsUnique);
