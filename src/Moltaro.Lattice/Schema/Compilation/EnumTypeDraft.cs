namespace Moltaro.Lattice.Schema.Compilation;

// An enum type as read from a descriptor, before validation.
internal sealed record EnumTypeDraft(
    int Id,
    string? Name,
    LatticeFieldTypeEnum UnderlyingType,
    bool IsFlags,
    IReadOnlyList<EnumMemberDraft> Members);
