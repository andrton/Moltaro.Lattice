namespace Moltaro.Lattice.Schema;

// The single place that validates a schema, whether it comes from JSON or from the binary form.
internal static class SchemaBuilder
{
    public static LatticeEntityType BuildEntityType(int id, string? name, string? clrTypeName, IReadOnlyList<FieldDraft> drafts, int keyFieldId, IReadOnlyList<IndexDraft> indexDrafts)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw Invalid($"entity type {id} has no name.");
        }

        if (clrTypeName is not null && string.IsNullOrWhiteSpace(clrTypeName))
        {
            throw Invalid($"entity type '{name}' has an empty ClrType.");
        }

        var fields = new LatticeField[drafts.Count];
        var ids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        LatticeField? key = null;

        for (var ordinal = 0; ordinal < drafts.Count; ordinal++)
        {
            var draft = drafts[ordinal];
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                throw Invalid($"field {draft.Id} of entity type '{name}' has no name.");
            }

            if (!Enum.IsDefined(draft.Type))
            {
                throw Invalid($"field '{name}.{draft.Name}' has an unknown type.");
            }

            if (!ids.Add(draft.Id) || !names.Add(draft.Name))
            {
                throw Invalid($"field '{name}.{draft.Name}' (id {draft.Id}) is declared twice.");
            }

            fields[ordinal] = new LatticeField(draft.Id, draft.Name, draft.Type, draft.IsNullable, ordinal);
            if (draft.Id == keyFieldId)
            {
                key = fields[ordinal];
            }
        }

        if (key is null)
        {
            throw Invalid($"entity type '{name}' has no field with the primary key id {keyFieldId}.");
        }

        if (key.IsNullable || !FieldTypes.CanBeKey(key.Type))
        {
            throw Invalid($"primary key '{name}.{key.Name}' must be a non-nullable integer, String or Guid field.");
        }

        return new LatticeEntityType(id, name, clrTypeName, fields, key, BuildIndexes(name, fields, indexDrafts));
    }

    private static LatticeIndex[] BuildIndexes(string entityName, LatticeField[] fields, IReadOnlyList<IndexDraft> drafts)
    {
        var indexes = new LatticeIndex[drafts.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var position = 0; position < drafts.Count; position++)
        {
            var draft = drafts[position];
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                throw Invalid($"an index of entity type '{entityName}' has no name.");
            }

            if (!names.Add(draft.Name))
            {
                throw Invalid($"index '{entityName}.{draft.Name}' is declared twice.");
            }

            if (draft.FieldIds.Count == 0)
            {
                throw Invalid($"index '{entityName}.{draft.Name}' has no fields.");
            }

            var indexFields = new LatticeField[draft.FieldIds.Count];
            for (var part = 0; part < indexFields.Length; part++)
            {
                var field = Array.Find(fields, candidate => candidate.Id == draft.FieldIds[part])
                    ?? throw Invalid($"index '{entityName}.{draft.Name}' refers to the field id {draft.FieldIds[part]}, which the entity type does not have.");

                if (field.Type == LatticeFieldTypeEnum.Binary)
                {
                    throw Invalid($"index '{entityName}.{draft.Name}' covers the Binary field '{field.Name}'; binary fields cannot be indexed.");
                }

                if (Array.IndexOf(indexFields, field, 0, part) >= 0)
                {
                    throw Invalid($"index '{entityName}.{draft.Name}' covers the field '{field.Name}' twice.");
                }

                indexFields[part] = field;
            }

            indexes[position] = new LatticeIndex(draft.Name, indexFields, draft.IsUnique);
        }

        return indexes;
    }

    public static LatticeSchema BuildSchema(int version, IReadOnlyList<LatticeEntityType> entityTypes)
    {
        var ids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var clrTypeNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entityType in entityTypes)
        {
            if (!ids.Add(entityType.Id) || !names.Add(entityType.Name))
            {
                throw Invalid($"entity type '{entityType.Name}' (id {entityType.Id}) is declared twice.");
            }

            if (entityType.ClrTypeName is { } clrTypeName && !clrTypeNames.Add(clrTypeName))
            {
                throw Invalid($".NET class '{clrTypeName}' is assigned to more than one entity type.");
            }
        }

        return new LatticeSchema(version, entityTypes);
    }

    public static LatticeException Invalid(string message, Exception? innerException = null) =>
        new(LatticeErrorEnum.InvalidSchema, $"The schema is not valid: {message}", innerException);
}
