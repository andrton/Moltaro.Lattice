namespace Moltaro.Lattice.Schema;

// Builds the secondary indexes of an entity type from their drafts, for SchemaBuilder.
//
// A part of an index is a path of fields: a field of the entity type, and from there through
// owned values to the indexed field. Every step but the last is an Owned field, so a path
// never passes through a List, a Map or a Set; the indexed field has an equality of its own,
// see FieldTypes.HasEquality. An index names a part once: two paths are the same part when
// they have the same fields in the same order, whether a path of one field was written as its
// identity or as a list of it.
internal static class IndexBuilder
{
    public static LatticeIndex[] Build(string entityName, IReadOnlyList<LatticeField> fields, IReadOnlyList<IndexDraft> drafts)
    {
        var indexes = new LatticeIndex[drafts.Count];
        var ids = new Dictionary<int, string>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var position = 0; position < drafts.Count; position++)
        {
            var draft = drafts[position];
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                throw SchemaBuilder.Invalid($"an index of entity type '{SchemaBuilder.Shown(entityName)}' has no name.");
            }

            var place = $"index '{SchemaBuilder.Shown(entityName)}.{SchemaBuilder.Shown(draft.Name)}'";
            SchemaBuilder.CheckIdentity(draft.Id, place, ids);
            if (!names.Add(draft.Name))
            {
                throw SchemaBuilder.Invalid($"{place} is declared twice.");
            }

            if (draft.Parts.Count == 0)
            {
                throw SchemaBuilder.Invalid($"{place} has no fields.");
            }

            var parts = new LatticeIndexPart[draft.Parts.Count];
            for (var part = 0; part < parts.Length; part++)
            {
                var path = BuildPath(entityName, fields, draft.Parts[part], place);
                if (Array.FindIndex(parts, 0, part, other => other.Path.SequenceEqual(path)) >= 0)
                {
                    throw SchemaBuilder.Invalid($"{place} covers the field '{Spelled(path)}' twice.");
                }

                parts[part] = new LatticeIndexPart(path);
            }

            indexes[position] = new LatticeIndex(draft.Id, draft.Name, parts, draft.IsUnique);
        }

        return indexes;
    }

    private static LatticeField[] BuildPath(string entityName, IReadOnlyList<LatticeField> fields, IReadOnlyList<int> fieldIds, string place)
    {
        if (fieldIds.Count == 0)
        {
            throw SchemaBuilder.Invalid($"{place} has a part with an empty path; a part names at least one field.");
        }

        var path = new LatticeField[fieldIds.Count];
        var candidates = fields;
        var owner = $"entity type '{SchemaBuilder.Shown(entityName)}'";

        for (var step = 0; step < path.Length; step++)
        {
            var field = candidates.FirstOrDefault(candidate => candidate.Id == fieldIds[step])
                ?? throw SchemaBuilder.Invalid($"{place} refers to the field id {fieldIds[step]}, which {owner} does not have.");

            path[step] = field;
            if (step == path.Length - 1)
            {
                break;
            }

            var ownedType = field.Declaration.OwnedType
                ?? throw SchemaBuilder.Invalid($"{place} has a path through the {field.Declaration.Type} field '{SchemaBuilder.Shown(Spelled(path[..(step + 1)]))}'; every step of a path but the last is an Owned field.");

            candidates = ownedType.Fields;
            owner = $"owned type '{SchemaBuilder.Shown(ownedType.Name)}'";
        }

        var indexed = path[^1];
        if (!FieldTypes.HasEquality(indexed.Declaration.Type))
        {
            throw SchemaBuilder.Invalid($"{place} covers the {indexed.Declaration.Type} field '{SchemaBuilder.Shown(Spelled(path))}'; a part of an index ends at a scalar that is not Binary, an Enum or a Reference.");
        }

        return path;
    }

    // The fields of a path by name, from the entity type down: Home.City.
    private static string Spelled(IEnumerable<LatticeField> path) => string.Join(".", path.Select(step => step.Name));
}
