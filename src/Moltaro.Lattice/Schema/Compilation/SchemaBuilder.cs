namespace Moltaro.Lattice.Schema.Compilation;

// The single place that validates a schema, whether it comes from JSON or from the binary form.
// The readers turn what they read into drafts; every rule of the model is checked here and in
// the builders this class calls, EnumTypeBuilder, DeclarationBuilder, OwnedTypeContainment and
// IndexBuilder, and every refusal names the declaration it concerns.
//
// The parts of a schema are built in an order in which everything a declaration names exists
// before the declaration: the enum types; the names, identities and classes of the entity
// types and the owned types, each kind a scope of its own; the primary keys of the entity
// types, which a reference holds and compares as; the owned types, each after the owned types
// it contains; the entity types with their fields and indexes; the fingerprints.
internal static class SchemaBuilder
{
    // By the owner's answer to Q28 the declaration of a field is at most this deep, counted
    // through collections and owned types together, see LatticeValueDeclaration.Depth. It is a
    // rule of the schema language, the same in every process, and not an option: a schema
    // means the same on every node. Every walk over a value or a class therefore goes no deeper,
    // and the reader of a saved schema stops at it while it reads.
    public const int MaximumDepth = 32;

    public static LatticeSchema BuildSchema(
        int version,
        LatticeStringEncodingEnum stringEncoding,
        IReadOnlyList<EnumTypeDraft> enumTypeDrafts,
        IReadOnlyList<OwnedTypeDraft> ownedTypeDrafts,
        IReadOnlyList<EntityTypeDraft> entityTypeDrafts)
    {
        if (version < 1)
        {
            throw Invalid($"the SchemaVersion is {version}; a catalog version is a whole number from 1.");
        }

        if (!Enum.IsDefined(stringEncoding))
        {
            throw Invalid($"the string encoding {stringEncoding} is not known.");
        }

        var enumTypes = EnumTypeBuilder.Build(enumTypeDrafts);

        // A .NET class is named by at most one type of the schema, entity type or owned type,
        // so that a class always means one type.
        var classNames = new HashSet<string>(StringComparer.Ordinal);
        CheckEntityTypes(entityTypeDrafts, classNames);
        CheckOwnedTypes(ownedTypeDrafts, classNames);

        var declarations = new DeclarationBuilder(enumTypes);
        foreach (var draft in entityTypeDrafts)
        {
            declarations.AddTarget(new ReferenceTarget(draft.Name!, draft.Id, BuildKey(draft, declarations)));
        }

        var ownedTypes = new LatticeOwnedType[ownedTypeDrafts.Count];
        foreach (var position in OwnedTypeContainment.BuildOrder(ownedTypeDrafts))
        {
            ownedTypes[position] = BuildOwnedType(ownedTypeDrafts[position], declarations);
            declarations.AddOwnedType(ownedTypes[position]);
        }

        var entityTypes = new LatticeEntityType[entityTypeDrafts.Count];
        for (var position = 0; position < entityTypes.Length; position++)
        {
            entityTypes[position] = BuildEntityType(entityTypeDrafts[position], stringEncoding, declarations);
        }

        var fingerprint = SchemaFingerprints.Schema(stringEncoding, enumTypes, ownedTypes, entityTypes);
        return new LatticeSchema(version, stringEncoding, enumTypes, ownedTypes, entityTypes, fingerprint);
    }

    // Entity types are a scope of their own for identities and names.
    private static void CheckEntityTypes(IReadOnlyList<EntityTypeDraft> drafts, HashSet<string> classNames)
    {
        var ids = new Dictionary<int, string>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var draft in drafts)
        {
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                throw Invalid($"entity type {draft.Id} has no name.");
            }

            var place = $"entity type '{Shown(draft.Name)}'";
            CheckIdentity(draft.Id, place, ids);
            if (!names.Add(draft.Name))
            {
                throw Invalid($"{place} is declared twice.");
            }

            CheckClass(draft.ClrTypeName, place, classNames);
        }
    }

    // Owned types are a scope of their own for identities and names, as enum types and entity
    // types are: an owned type may have the identity or the name of a type of another kind,
    // because a declaration names a type of one kind.
    private static void CheckOwnedTypes(IReadOnlyList<OwnedTypeDraft> drafts, HashSet<string> classNames)
    {
        var ids = new Dictionary<int, string>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var draft in drafts)
        {
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                throw Invalid($"owned type {draft.Id} has no name.");
            }

            var place = $"owned type '{Shown(draft.Name)}'";
            CheckIdentity(draft.Id, place, ids);
            if (!names.Add(draft.Name))
            {
                throw Invalid($"{place} is declared twice.");
            }

            CheckClass(draft.ClrTypeName, place, classNames);
            if (draft.Fields.Count == 0)
            {
                throw Invalid($"{place} declares no fields; an owned type declares at least one.");
            }
        }
    }

    private static void CheckClass(string? clrTypeName, string place, HashSet<string> classNames)
    {
        if (clrTypeName is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(clrTypeName))
        {
            throw Invalid($"{place} has an empty ClrType.");
        }

        if (!classNames.Add(clrTypeName))
        {
            throw Invalid($".NET class '{Shown(clrTypeName)}' is named by more than one type of the schema; a class is the class of one entity type or one owned type.");
        }
    }

    // The declaration of the primary key of an entity type, built before any other field of
    // the schema because a reference to the entity type holds and compares a key.
    private static LatticeValueDeclaration BuildKey(EntityTypeDraft draft, DeclarationBuilder declarations)
    {
        var name = draft.Name!;
        var position = -1;
        for (var index = 0; index < draft.Fields.Count && position < 0; index++)
        {
            if (draft.Fields[index].Id == draft.KeyFieldId)
            {
                position = index;
            }
        }

        if (position < 0)
        {
            throw Invalid($"entity type '{Shown(name)}' has no field with the primary key id {draft.KeyFieldId}.");
        }

        var key = draft.Fields[position];
        if (string.IsNullOrWhiteSpace(key.Name))
        {
            throw Invalid($"field {key.Id} of entity type '{Shown(name)}' has no name.");
        }

        if (key.Declaration.IsNullable || !FieldTypes.CanBeKey(key.Declaration.Type))
        {
            throw Invalid($"primary key '{Shown(name)}.{Shown(key.Name)}' must be a non-nullable integer, String or Guid field.");
        }

        return declarations.Build(key.Declaration, $"field '{Shown(name)}.{Shown(key.Name)}'");
    }

    private static LatticeOwnedType BuildOwnedType(OwnedTypeDraft draft, DeclarationBuilder declarations)
    {
        var fields = BuildFields(draft.Fields, "owned type", draft.Name!, declarations, key: null);

        return new LatticeOwnedType(draft.Id, draft.Name!, draft.ClrTypeName, fields);
    }

    private static LatticeEntityType BuildEntityType(EntityTypeDraft draft, LatticeStringEncodingEnum stringEncoding, DeclarationBuilder declarations)
    {
        var name = draft.Name!;
        var fields = BuildFields(draft.Fields, "entity type", name, declarations, (draft.KeyFieldId, declarations.TargetOf(name).Key));
        var key = Array.Find(fields, field => field.Id == draft.KeyFieldId)!;

        var indexes = IndexBuilder.Build(name, fields, draft.Indexes);
        var layoutFingerprint = SchemaFingerprints.Layout(stringEncoding, key, fields);
        return new LatticeEntityType(draft.Id, name, draft.ClrTypeName, fields, key, indexes, stringEncoding, layoutFingerprint);
    }

    // The fields of an entity type or of an owned type, which is the scope of their identities
    // and names. The key of an entity type keeps the declaration it was built with, the one
    // that references to the entity type hold.
    private static LatticeField[] BuildFields(
        IReadOnlyList<FieldDraft> drafts,
        string ownerKind,
        string ownerName,
        DeclarationBuilder declarations,
        (int FieldId, LatticeValueDeclaration Declaration)? key)
    {
        var fields = new LatticeField[drafts.Count];
        var ids = new Dictionary<int, string>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var ordinal = 0; ordinal < fields.Length; ordinal++)
        {
            var draft = drafts[ordinal];
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                throw Invalid($"field {draft.Id} of {ownerKind} '{Shown(ownerName)}' has no name.");
            }

            var place = $"field '{Shown(ownerName)}.{Shown(draft.Name)}'";
            if (draft.Name.Contains('.', StringComparison.Ordinal))
            {
                throw Invalid($"{place} has a dot in its name. A dot separates fields in a path.");
            }

            CheckIdentity(draft.Id, place, ids);
            if (!names.Add(draft.Name))
            {
                throw Invalid($"{place} is declared twice.");
            }

            var declaration = key is { } primaryKey && draft.Id == primaryKey.FieldId
                ? primaryKey.Declaration
                : declarations.Build(draft.Declaration, place);

            if (declaration.Depth > MaximumDepth)
            {
                throw Invalid($"{place} is {declaration.Depth} deep; a declaration is at most {MaximumDepth} deep, counted through collections and owned types together.");
            }

            fields[ordinal] = new LatticeField(draft.Id, draft.Name, declaration, ordinal);
        }

        return fields;
    }

    // An identity is at least 1 and unique within its scope: the schema for enum types, for
    // owned types and for entity types, each apart; the entity type or the owned type for
    // fields, the entity type for indexes. Of two declarations with the same identity, the one
    // that repeats it is refused.
    public static void CheckIdentity(int id, string place, Dictionary<int, string> scope)
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
