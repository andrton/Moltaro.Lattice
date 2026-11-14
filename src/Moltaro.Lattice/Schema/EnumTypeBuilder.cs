namespace Moltaro.Lattice.Schema;

// Builds the enum types of a schema from their drafts, for SchemaBuilder: identities and names
// unique among enum types, an integer underlying type, at least one member, values within the
// range of that type, one canonical name per value, and the rules of Flags.
internal static class EnumTypeBuilder
{
    public static LatticeEnumType[] Build(IReadOnlyList<EnumTypeDraft> drafts)
    {
        var enumTypes = new LatticeEnumType[drafts.Count];
        var ids = new Dictionary<int, string>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var position = 0; position < enumTypes.Length; position++)
        {
            var draft = drafts[position];
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                throw SchemaBuilder.Invalid($"enum type {draft.Id} has no name.");
            }

            var place = $"enum type '{SchemaBuilder.Shown(draft.Name)}'";
            SchemaBuilder.CheckIdentity(draft.Id, place, ids);
            if (!names.Add(draft.Name))
            {
                throw SchemaBuilder.Invalid($"{place} is declared twice.");
            }

            if (!FieldTypes.IsInteger(draft.UnderlyingType))
            {
                throw SchemaBuilder.Invalid($"{place} has the underlying type {draft.UnderlyingType}; it must be one of the eight integer types, Int8 to UInt64.");
            }

            // Without a member an ordinary enum type has no value at all, and a Flags type only
            // the empty set, which no member names.
            if (draft.Members.Count == 0)
            {
                throw SchemaBuilder.Invalid($"{place} declares no members; an enum type declares at least one, a Flags type as well.");
            }

            enumTypes[position] = new LatticeEnumType(draft.Id, draft.Name, draft.UnderlyingType, draft.IsFlags, BuildMembers(draft));
        }

        return enumTypes;
    }

    private static LatticeEnumMember[] BuildMembers(EnumTypeDraft draft)
    {
        var range = IntegerRange.Of(draft.UnderlyingType);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var namesOfValue = new Dictionary<Int128, int>();
        var canonicalNamesOfValue = new Dictionary<Int128, int>();

        foreach (var member in draft.Members)
        {
            if (string.IsNullOrWhiteSpace(member.Name))
            {
                throw SchemaBuilder.Invalid($"a member of enum type '{SchemaBuilder.Shown(draft.Name)}' has no name.");
            }

            if (!names.Add(member.Name))
            {
                throw SchemaBuilder.Invalid($"member '{SchemaBuilder.Shown(draft.Name)}.{SchemaBuilder.Shown(member.Name)}' is declared twice.");
            }

            if (!range.Contains(member.Value))
            {
                throw SchemaBuilder.Invalid($"member '{SchemaBuilder.Shown(draft.Name)}.{SchemaBuilder.Shown(member.Name)}' has the value {member.Value}, outside the range of {draft.UnderlyingType}.");
            }

            namesOfValue[member.Value] = namesOfValue.GetValueOrDefault(member.Value) + 1;
            if (member.IsCanonical)
            {
                canonicalNamesOfValue[member.Value] = canonicalNamesOfValue.GetValueOrDefault(member.Value) + 1;
            }
        }

        // A value with several names has exactly one canonical name, which the descriptor
        // marks: the order of declaration does not choose it.
        foreach (var member in draft.Members)
        {
            var nameCount = namesOfValue[member.Value];
            var canonicalCount = canonicalNamesOfValue.GetValueOrDefault(member.Value);
            if (nameCount > 1 && canonicalCount != 1)
            {
                throw SchemaBuilder.Invalid($"the value {member.Value} of enum type '{SchemaBuilder.Shown(draft.Name)}' has {nameCount} names and {canonicalCount} of them marked Canonical; exactly one must be.");
            }
        }

        if (draft.IsFlags)
        {
            CheckFlags(draft, range);
        }

        // The only name of a value is canonical, marked or not.
        return [.. draft.Members.Select(member => new LatticeEnumMember(member.Name!, member.Value, member.IsCanonical || namesOfValue[member.Value] == 1))];
    }

    // A flag is a member with exactly one bit within the width of the underlying type; the
    // sign bit of a signed type is one bit like the others. A member with several bits names a
    // combination, which adds no bit: it may have only bits that flags declare. Zero names
    // the empty set.
    private static void CheckFlags(EnumTypeDraft draft, IntegerRange range)
    {
        var flagBits = UInt128.Zero;
        foreach (var member in draft.Members)
        {
            var bits = range.BitsOf(member.Value);
            if (UInt128.PopCount(bits) == UInt128.One)
            {
                flagBits |= bits;
            }
        }

        foreach (var member in draft.Members)
        {
            if ((range.BitsOf(member.Value) & ~flagBits) != UInt128.Zero)
            {
                throw SchemaBuilder.Invalid($"member '{SchemaBuilder.Shown(draft.Name)}.{SchemaBuilder.Shown(member.Name)}' of the Flags enum type has the value {member.Value}, with a bit that no flag declares; a member that names a combination has only bits of members with one bit.");
            }
        }
    }
}
