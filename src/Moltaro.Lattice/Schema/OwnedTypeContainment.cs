namespace Moltaro.Lattice.Schema;

// The order in which SchemaBuilder builds the owned types: every owned type after the owned
// types it contains, so that the owned type a declaration names is built before it.
//
// What an owned type contains is what its values own: the owned types of its Owned fields, of
// the elements of its Lists and of the values of its Maps, through collections inside
// collections, and whatever those owned types contain in turn. A Reference is not
// containment, and a Map key and a Set element never hold an owned value. An owned type does
// not contain itself, so the relation has no cycle and the order exists; a declaration that
// leads from an owned type back to itself is refused with the owned types on the way named.
//
// The walk keeps its own stack instead of recursing from owned type to owned type, so that a
// long chain of owned types does not reach the depth of the call stack. A name that no owned
// type has is left out here; the declaration that names it is refused when it is built.
internal static class OwnedTypeContainment
{
    // The positions of the drafts in the order to build them. The names of the owned types are
    // checked unique before.
    public static int[] BuildOrder(IReadOnlyList<OwnedTypeDraft> drafts)
    {
        var positionByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var position = 0; position < drafts.Count; position++)
        {
            positionByName.Add(drafts[position].Name!, position);
        }

        var contained = new List<int>[drafts.Count];
        for (var position = 0; position < drafts.Count; position++)
        {
            contained[position] = [];
            foreach (var field in drafts[position].Fields)
            {
                Collect(field.Declaration, positionByName, contained[position]);
            }
        }

        // A depth-first walk in the order of the declarations: an owned type is on the way
        // while the walk is inside it, and done when everything it contains is.
        var order = new List<int>(drafts.Count);
        var onTheWay = new bool[drafts.Count];
        var done = new bool[drafts.Count];
        var way = new List<(int OwnedType, int Next)>();

        for (var start = 0; start < drafts.Count; start++)
        {
            if (done[start])
            {
                continue;
            }

            onTheWay[start] = true;
            way.Add((start, 0));
            while (way.Count > 0)
            {
                var (ownedType, next) = way[^1];
                if (next == contained[ownedType].Count)
                {
                    way.RemoveAt(way.Count - 1);
                    onTheWay[ownedType] = false;
                    done[ownedType] = true;
                    order.Add(ownedType);
                    continue;
                }

                way[^1] = (ownedType, next + 1);
                var target = contained[ownedType][next];
                if (onTheWay[target])
                {
                    throw ContainsItself(drafts, way, target);
                }

                if (!done[target])
                {
                    onTheWay[target] = true;
                    way.Add((target, 0));
                }
            }
        }

        return [.. order];
    }

    // The owned types a position holds as its own values. The recursion follows the nesting of
    // one declaration, which the reader of each form bounds.
    private static void Collect(ValueDeclarationDraft draft, Dictionary<string, int> positionByName, List<int> contained)
    {
        switch (draft.Type)
        {
            case LatticeFieldTypeEnum.Owned when draft.OwnedTypeName is { } name && positionByName.TryGetValue(name, out var position):
                contained.Add(position);
                break;

            case LatticeFieldTypeEnum.List when draft.Element is { } element:
                Collect(element, positionByName, contained);
                break;

            case LatticeFieldTypeEnum.Map when draft.Value is { } value:
                Collect(value, positionByName, contained);
                break;
        }
    }

    private static LatticeException ContainsItself(IReadOnlyList<OwnedTypeDraft> drafts, List<(int OwnedType, int Next)> way, int target)
    {
        var first = way.FindIndex(step => step.OwnedType == target);
        var names = way
            .Skip(first)
            .Select(step => step.OwnedType)
            .Append(target)
            .Select(position => $"'{SchemaBuilder.Shown(drafts[position].Name)}'");

        return SchemaBuilder.Invalid(
            $"owned type '{SchemaBuilder.Shown(drafts[target].Name)}' contains itself: {string.Join(" holds ", names)}. An owned type does not contain itself, directly or through other owned types.");
    }
}
