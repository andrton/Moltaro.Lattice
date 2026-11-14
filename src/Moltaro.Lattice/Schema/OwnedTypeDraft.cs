namespace Moltaro.Lattice.Schema;

// An owned type as read from a descriptor, before validation.
internal sealed record OwnedTypeDraft(
    int Id,
    string? Name,
    string? ClrTypeName,
    IReadOnlyList<FieldDraft> Fields);
