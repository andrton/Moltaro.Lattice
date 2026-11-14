namespace Moltaro.Lattice.Schema.Compilation;

// An entity type as read from a descriptor, before validation.
internal sealed record EntityTypeDraft(
    int Id,
    string? Name,
    string? ClrTypeName,
    IReadOnlyList<FieldDraft> Fields,
    int KeyFieldId,
    IReadOnlyList<IndexDraft> Indexes);
