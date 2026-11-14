namespace Moltaro.Lattice.Schema;

// The single place that validates a schema, whether it comes from JSON or from the binary form.
// The readers turn what they read into drafts; every rule of the model is checked here, and
// every refusal names the declaration it concerns.
internal static class SchemaBuilder
{
    public static LatticeSchema BuildSchema(int version, LatticeStringEncodingEnum stringEncoding, IReadOnlyList<EnumTypeDraft> enumTypeDrafts, IReadOnlyList<EntityTypeDraft> entityTypeDrafts)
    {
        if (!Enum.IsDefined(stringEncoding))
        {
            throw Invalid($"the string encoding {stringEncoding} is not known.");
        }

        var enumTypes = BuildEnumTypes(enumTypeDrafts);
        var enumTypesByName = enumTypes.ToDictionary(enumType => enumType.Name, StringComparer.Ordinal);

        var entityTypes = new LatticeEntityType[entityTypeDrafts.Count];
        var ids = new Dictionary<int, string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var clrTypeNames = new HashSet<string>(StringComparer.Ordinal);

        for (var position = 0; position < entityTypes.Length; position++)
        {
            var entityType = BuildEntityType(entityTypeDrafts[position], stringEncoding, enumTypesByName);
            var place = $"entity type '{Shown(entityType.Name)}'";

            CheckIdentity(entityType.Id, place, ids);
            if (!names.Add(entityType.Name))
            {
                throw Invalid($"{place} is declared twice.");
            }

            if (entityType.ClrTypeName is { } clrTypeName && !clrTypeNames.Add(clrTypeName))
            {
                throw Invalid($".NET class '{Shown(clrTypeName)}' is assigned to more than one entity type.");
            }

            entityTypes[position] = entityType;
        }

        var fingerprint = SchemaFingerprints.Schema(stringEncoding, enumTypes, entityTypes);
        return new LatticeSchema(version, stringEncoding, enumTypes, entityTypes, fingerprint);
    }

    private static LatticeEnumType[] BuildEnumTypes(IReadOnlyList<EnumTypeDraft> drafts)
    {
        var enumTypes = new LatticeEnumType[drafts.Count];
        var ids = new Dictionary<int, string>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var position = 0; position < enumTypes.Length; position++)
        {
            var draft = drafts[position];
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                throw Invalid($"enum type {draft.Id} has no name.");
            }

            var place = $"enum type '{Shown(draft.Name)}'";
            CheckIdentity(draft.Id, place, ids);
            if (!names.Add(draft.Name))
            {
                throw Invalid($"{place} is declared twice.");
            }

            if (!FieldTypes.IsInteger(draft.UnderlyingType))
            {
                throw Invalid($"{place} has the underlying type {draft.UnderlyingType}; it must be one of the eight integer types, Int8 to UInt64.");
            }

            enumTypes[position] = new LatticeEnumType(draft.Id, draft.Name, draft.UnderlyingType, draft.IsFlags, BuildEnumMembers(draft));
        }

        return enumTypes;
    }

    private static LatticeEnumMember[] BuildEnumMembers(EnumTypeDraft draft)
    {
        var range = IntegerRange.Of(draft.UnderlyingType);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var namesOfValue = new Dictionary<Int128, int>();
        var canonicalNamesOfValue = new Dictionary<Int128, int>();

        foreach (var member in draft.Members)
        {
            if (string.IsNullOrWhiteSpace(member.Name))
            {
                throw Invalid($"a member of enum type '{Shown(draft.Name)}' has no name.");
            }

            if (!names.Add(member.Name))
            {
                throw Invalid($"member '{Shown(draft.Name)}.{Shown(member.Name)}' is declared twice.");
            }

            if (!range.Contains(member.Value))
            {
                throw Invalid($"member '{Shown(draft.Name)}.{Shown(member.Name)}' has the value {member.Value}, outside the range of {draft.UnderlyingType}.");
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
                throw Invalid($"the value {member.Value} of enum type '{Shown(draft.Name)}' has {nameCount} names and {canonicalCount} of them marked Canonical; exactly one must be.");
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
                throw Invalid($"member '{Shown(draft.Name)}.{Shown(member.Name)}' of the Flags enum type has the value {member.Value}, with a bit that no flag declares; a member that names a combination has only bits of members with one bit.");
            }
        }
    }

    private static LatticeEntityType BuildEntityType(EntityTypeDraft draft, LatticeStringEncodingEnum stringEncoding, Dictionary<string, LatticeEnumType> enumTypes)
    {
        var name = draft.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw Invalid($"entity type {draft.Id} has no name.");
        }

        if (draft.ClrTypeName is not null && string.IsNullOrWhiteSpace(draft.ClrTypeName))
        {
            throw Invalid($"entity type '{Shown(name)}' has an empty ClrType.");
        }

        var fields = new LatticeField[draft.Fields.Count];
        var ids = new Dictionary<int, string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        LatticeField? key = null;

        for (var ordinal = 0; ordinal < fields.Length; ordinal++)
        {
            var fieldDraft = draft.Fields[ordinal];
            if (string.IsNullOrWhiteSpace(fieldDraft.Name))
            {
                throw Invalid($"field {fieldDraft.Id} of entity type '{Shown(name)}' has no name.");
            }

            var place = $"field '{Shown(name)}.{Shown(fieldDraft.Name)}'";
            CheckIdentity(fieldDraft.Id, place, ids);
            if (!names.Add(fieldDraft.Name))
            {
                throw Invalid($"{place} is declared twice.");
            }

            fields[ordinal] = new LatticeField(fieldDraft.Id, fieldDraft.Name, BuildDeclaration(fieldDraft.Declaration, place, enumTypes), ordinal);
            if (fieldDraft.Id == draft.KeyFieldId)
            {
                key = fields[ordinal];
            }
        }

        if (key is null)
        {
            throw Invalid($"entity type '{Shown(name)}' has no field with the primary key id {draft.KeyFieldId}.");
        }

        if (key.Declaration.IsNullable || !FieldTypes.CanBeKey(key.Declaration.Type))
        {
            throw Invalid($"primary key '{Shown(name)}.{Shown(key.Name)}' must be a non-nullable integer, String or Guid field.");
        }

        var indexes = BuildIndexes(name, fields, draft.Indexes);
        var layoutFingerprint = SchemaFingerprints.Layout(stringEncoding, fields);
        return new LatticeEntityType(draft.Id, name, draft.ClrTypeName, fields, key, indexes, layoutFingerprint);
    }

    // A policy belongs to the type it applies to. Stated for another type it would mean
    // nothing, so it is a mistake in the descriptor rather than something to ignore.
    private static LatticeValueDeclaration BuildDeclaration(ValueDeclarationDraft draft, string place, Dictionary<string, LatticeEnumType> enumTypes)
    {
        if (!Enum.IsDefined(draft.Type))
        {
            throw Invalid($"{place} has an unknown type.");
        }

        var isString = draft.Type == LatticeFieldTypeEnum.String;
        if (!isString && (draft.Comparison is not null || draft.StringStorage is not null))
        {
            throw Invalid($"{place} is {draft.Type}; a Comparison and a StringStorage apply only to a String.");
        }

        if (draft.Comparison is { } comparison && !Enum.IsDefined(comparison))
        {
            throw Invalid($"{place} has an unknown comparison.");
        }

        if (draft.StringStorage is { } stringStorage && !Enum.IsDefined(stringStorage))
        {
            throw Invalid($"{place} has an unknown string storage.");
        }

        LatticeEnumType? enumType = null;
        if (draft.Type == LatticeFieldTypeEnum.Enum)
        {
            if (draft.EnumTypeName is null)
            {
                throw Invalid($"{place} is of type Enum and names no enum type.");
            }

            if (!enumTypes.TryGetValue(draft.EnumTypeName, out enumType))
            {
                throw Invalid($"{place} names the enum type '{Shown(draft.EnumTypeName)}', which the schema does not declare.");
            }
        }
        else if (draft.EnumTypeName is not null)
        {
            throw Invalid($"{place} is {draft.Type} and names the enum type '{Shown(draft.EnumTypeName)}'; only a field of type Enum has one.");
        }

        return new LatticeValueDeclaration(
            draft.Type,
            draft.IsNullable,
            isString ? draft.Comparison ?? LatticeStringComparisonEnum.Ordinal : null,
            isString ? draft.StringStorage ?? LatticeStringStorageEnum.Plain : null,
            enumType);
    }

    private static LatticeIndex[] BuildIndexes(string entityName, LatticeField[] fields, IReadOnlyList<IndexDraft> drafts)
    {
        var indexes = new LatticeIndex[drafts.Count];
        var ids = new Dictionary<int, string>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var position = 0; position < drafts.Count; position++)
        {
            var draft = drafts[position];
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                throw Invalid($"an index of entity type '{Shown(entityName)}' has no name.");
            }

            var place = $"index '{Shown(entityName)}.{Shown(draft.Name)}'";
            CheckIdentity(draft.Id, place, ids);
            if (!names.Add(draft.Name))
            {
                throw Invalid($"{place} is declared twice.");
            }

            if (draft.FieldIds.Count == 0)
            {
                throw Invalid($"{place} has no fields.");
            }

            var indexFields = new LatticeField[draft.FieldIds.Count];
            for (var part = 0; part < indexFields.Length; part++)
            {
                var field = Array.Find(fields, candidate => candidate.Id == draft.FieldIds[part])
                    ?? throw Invalid($"{place} refers to the field id {draft.FieldIds[part]}, which the entity type does not have.");

                if (field.Declaration.Type == LatticeFieldTypeEnum.Binary)
                {
                    throw Invalid($"{place} covers the Binary field '{Shown(field.Name)}'; binary fields cannot be indexed.");
                }

                if (Array.IndexOf(indexFields, field, 0, part) >= 0)
                {
                    throw Invalid($"{place} covers the field '{Shown(field.Name)}' twice.");
                }

                indexFields[part] = field;
            }

            indexes[position] = new LatticeIndex(draft.Id, draft.Name, indexFields, draft.IsUnique);
        }

        return indexes;
    }

    // An identity is at least 1 and unique within its scope: the schema for enum types and for
    // entity types, the entity type for fields and for indexes. Of two declarations with the
    // same identity, the one that repeats it is refused.
    private static void CheckIdentity(int id, string place, Dictionary<int, string> scope)
    {
        if (id < 1)
        {
            throw Invalid($"{place} has the identity {id}; an identity is at least 1.");
        }

        if (!scope.TryAdd(id, place))
        {
            throw Invalid($"{place} has the identity {id}, which {scope[id]} already has.");
        }
    }

    public static LatticeException Invalid(string message, Exception? innerException = null) =>
        new(LatticeErrorEnum.InvalidSchema, $"The schema is not valid: {message}", innerException);

    // Text of the input repeated in a message: a name or a number of a descriptor has no
    // length limit, and a message stays readable whatever its length. A longer text is cut,
    // never inside a surrogate pair, and its length is told.
    public static string Shown(string? text)
    {
        const int ShownLength = 64;
        if (text is null || text.Length <= ShownLength)
        {
            return text ?? "";
        }

        var length = char.IsHighSurrogate(text[ShownLength - 1]) ? ShownLength - 1 : ShownLength;
        return $"{text[..length]}... ({text.Length} characters)";
    }
}
