using System.Collections;
using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots.Edits;

// An edit starts with the stored entries or with the whole value a command
// assigned. Each effective operation advances the local lookup form.
internal sealed class EditedCollection
{
    private readonly EditedList? _list;
    private readonly EditedAssociation? _association;
    private bool _hashed;

    public EditedCollection(StructureLayout element, StoredCollection? stored, object? assigned)
    {
        Stored = stored;
        Changed = assigned is not null;
        var count = stored?.Count ?? element.CountEntries(assigned!);
        if (element.Collection!.Type == LatticeFieldTypeEnum.List)
        {
            _list = new EditedList(count, (IList?)assigned);
        }
        else
        {
            _association = new EditedAssociation(element, stored, assigned);
            _hashed = stored?.Hashed ?? !CollectionLookup.IsCompact(count);
        }
    }

    public StoredCollection? Stored { get; }

    public bool Changed { get; private set; }

    public int Count => _list?.Count ?? _association!.Count;

    public bool Edit(in UpdateCommand command, LatticeEntityType entityType, StructureLayout element)
    {
        if (_list is { } list)
        {
            if (!list.Apply(command))
            {
                throw RootUpdate.TargetMissing(entityType, command.Name, $"has no List position {command.Position} for {command.Kind}.");
            }
        }
        else
        {
            if (!_association!.Apply(command))
            {
                return false;
            }

            _hashed = !CollectionLookup.IsCompactAfterEdit(Count, _hashed);
        }

        RequireResultCount(element, entityType, command.Name, Count);
        Changed = true;
        return true;
    }

    // All effective edits reach the same layout bound before placement.
    public static void RequireResultCount(StructureLayout element, LatticeEntityType entityType, string fieldName, int count)
    {
        if (count > element.MostElements)
        {
            throw new LatticeException(LatticeErrorEnum.ValidationFailed,
                $"{entityType.Name}.{fieldName} would hold {count} entries, more than its limit of {element.MostElements}.");
        }
    }

    public IEnumerator<EditedEntry> OpenEntries() => (_list?.Entries() ?? _association!.Entries()).GetEnumerator();

    public void Place(ref ValueHolder holder, StructureLayout structure, int target)
    {
        ref readonly var place = ref structure.Place(target);
        var element = structure.ElementOf(target);
        var source = new Placement(this);
        if (_list is not null)
        {
            source.Place(ref holder, place, element, place.Offset, Count, allowInline: true);
        }
        else
        {
            NestedValues.PlaceCollection(ref holder, place, element, place.Offset, Count, compact: !_hashed, ref source);
        }

        if (!holder.Refused)
        {
            NestedValues.MarkPresent(ref holder, structure, 0, place);
        }
    }

    private readonly struct Placement(EditedCollection edits) : ICollectionEntrySource
    {
        public void Place(ref ValueHolder holder, in StoredField place, StructureLayout element, int offset, int count, bool allowInline)
        {
            using var entries = edits.OpenEntries();
            if (element.HoldsDescriptors)
            {
                using var buffer = new EditedRecords(holder.Arena, holder.Encoding, edits.Stored, entries, Math.Min(element.RecordsPerChunk, count));
                // The placement receives a value copy. It only writes the
                // buffer and advances the cursor, both owned by this scope.
                var records = buffer;
                NestedValues.PlaceRecords(ref holder, place, element, offset, count, ref records);
            }
            else
            {
                EditedPacked.Place(ref holder, place, element, offset, count, edits.Stored, entries, allowInline);
            }
        }
    }
}
