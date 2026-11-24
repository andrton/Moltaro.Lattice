using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage.Roots.Edits;

// Changes keyed by original ordinal, plus new entries under the declared
// comparison. Native probes consult the old lookup. Final placement reads
// the active original ordinals and then the surviving new entries.
internal sealed class EditedAssociation
{
    private static readonly object NullMember = new();

    private readonly StoredCollection? _stored;
    private readonly Dictionary<int, EditedEntry> _changed = [];
    private readonly List<EditedEntry> _added = [];
    private readonly Dictionary<object, int> _newKeys;

    public EditedAssociation(StructureLayout element, StoredCollection? stored, object? assigned)
    {
        _stored = stored;
        Count = stored?.Count ?? 0;
        var declaration = element.DeclarationOf(0);
        _newKeys = new Dictionary<object, int>(new KeyComparison(declaration.TextComparison));

        if (assigned is not null)
        {
            var collection = element.Collection!;
            if (collection.Map is { } map)
            {
                foreach (var (key, value) in map.Entries(assigned))
                {
                    Add(key, value);
                }
            }
            else
            {
                foreach (var member in collection.Sequence!.Elements(assigned))
                {
                    Add(member, null);
                }
            }
        }
    }

    public int Count { get; private set; }

    public bool Apply(in UpdateCommand command)
    {
        var key = command.First;
        var setsValue = command.Kind == UpdateCommandKindEnum.MapSet;
        var adds = setsValue || command.Kind == UpdateCommandKindEnum.SetAdd;
        var original = _stored?.Find(key) ?? -1;
        if (original >= 0 && (!_changed.TryGetValue(original, out var prior) || prior.Original != EditedEntry.Removed))
        {
            if (setsValue)
            {
                _changed[original] = new EditedEntry(original, null, command.Second, ReplacesSecond: true);
                return true;
            }

            if (adds)
            {
                return false;
            }

            _changed[original] = new EditedEntry(EditedEntry.Removed, null, null);
            Count--;
            return true;
        }

        var comparisonKey = key ?? NullMember;
        if (_newKeys.TryGetValue(comparisonKey, out var index))
        {
            if (setsValue)
            {
                _added[index] = _added[index] with { Second = command.Second };
                return true;
            }

            if (adds)
            {
                return false;
            }

            _newKeys.Remove(comparisonKey);
            _added[index] = new EditedEntry(EditedEntry.Removed, null, null);
            Count--;
            return true;
        }

        if (!adds)
        {
            return false;
        }

        Add(key, command.Second);
        return true;
    }

    public IEnumerable<EditedEntry> Entries()
    {
        var original = _stored?.Count ?? 0;
        for (var ordinal = 0; ordinal < original; ordinal++)
        {
            if (!_changed.TryGetValue(ordinal, out var change))
            {
                yield return EditedEntry.Stored(ordinal);
            }
            else if (change.Original != EditedEntry.Removed)
            {
                yield return change;
            }
        }

        foreach (var entry in _added)
        {
            if (entry.Original != EditedEntry.Removed)
            {
                yield return entry;
            }
        }
    }

    private void Add(object? key, object? value)
    {
        _newKeys.Add(key ?? NullMember, _added.Count);
        _added.Add(new EditedEntry(EditedEntry.New, key, value));
        Count++;
    }

    private sealed class KeyComparison(TextComparison? text) : IEqualityComparer<object>
    {
        public new bool Equals(object? left, object? right) =>
            text is not null && left is string a && right is string b ? text.Equals(a, b) : object.Equals(left, right);

        public int GetHashCode(object key) => text is not null && key is string value ? text.GetHashCode(value) : key.GetHashCode();
    }
}
