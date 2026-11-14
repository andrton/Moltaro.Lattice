namespace Moltaro.Lattice.Schema;

// An enum member as read from a descriptor, before validation. IsCanonical is whether the
// descriptor marks it; SchemaBuilder makes the only name of a value canonical.
internal readonly record struct EnumMemberDraft(string? Name, Int128 Value, bool IsCanonical);
