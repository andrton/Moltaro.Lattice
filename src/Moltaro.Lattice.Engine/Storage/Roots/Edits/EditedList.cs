using System.Collections;
using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Budgeting;

namespace Moltaro.Lattice.Storage.Roots.Edits;

// Runs of original entries or single new elements. Checking a position and
// editing a run does not copy the list of unchanged elements into managed memory.
internal sealed class EditedList : IDisposable
{
    private readonly IList? _assigned;
    private readonly List<Run> _runs = [];
    private readonly MemoryBudget? _budget;

    public EditedList(int count, IList? assigned, MemoryBudget? budget)
    {
        Count = count;
        _assigned = assigned;
        _budget = budget;
        if (count != 0)
        {
            _runs.EnsureCapacity(1, _budget, nameof(EditedList));
            _runs.Add(new Run(0, count, null));
        }
    }

    public int Count { get; private set; }

    public bool Apply(in UpdateCommand command)
    {
        var position = command.Kind == UpdateCommandKindEnum.ListAppend ? Count : command.Position;
        var inserts = command.Kind is UpdateCommandKindEnum.ListInsert or UpdateCommandKindEnum.ListAppend;
        if (position < 0 || position > Count || (!inserts && position == Count))
        {
            return false;
        }

        // A replayed append is a ListInsert at the current count.
        if (position == Count)
        {
            _runs.EnsureCapacity(_runs.Count + 1, _budget, nameof(EditedList));
            _runs.Add(new Run(EditedEntry.New, 1, command.First));
            Count++;
            return true;
        }

        var place = 0;
        var within = position;
        while (within >= _runs[place].Count)
        {
            within -= _runs[place].Count;
            place++;
        }

        var deletes = !inserts;
        if (inserts && within == 0)
        {
            _runs.EnsureCapacity(_runs.Count + 1, _budget, nameof(EditedList));
            _runs.Insert(place, new Run(EditedEntry.New, 1, command.First));
        }
        else
        {
            var run = _runs[place];
            var consumed = within + (deletes ? 1 : 0);
            var pieces = (within != 0 ? 1 : 0) + (command.Kind != UpdateCommandKindEnum.ListRemove ? 1 : 0) +
                (consumed < run.Count ? 1 : 0);
            _runs.EnsureCapacity(_runs.Count - 1 + pieces, _budget, nameof(EditedList));
            _runs.RemoveAt(place);
            if (within != 0)
            {
                _runs.Insert(place++, run with { Count = within });
            }

            if (command.Kind != UpdateCommandKindEnum.ListRemove)
            {
                _runs.Insert(place++, new Run(EditedEntry.New, 1, command.First));
            }

            if (consumed < run.Count)
            {
                _runs.Insert(place, new Run(run.First + consumed, run.Count - consumed, run.Value));
            }
        }

        if (inserts)
        {
            Count++;
        }
        else if (command.Kind == UpdateCommandKindEnum.ListRemove)
        {
            Count--;
        }

        return true;
    }

    public void Dispose()
    {
        var backing = BudgetCollections.Items(_runs);
        _runs.Clear();
        _runs.Capacity = 0;
        _budget?.Release(backing);
    }

    public IEnumerable<EditedEntry> Entries()
    {
        foreach (var run in _runs)
        {
            if (run.First == EditedEntry.New)
            {
                yield return new EditedEntry(EditedEntry.New, run.Value, null);
                continue;
            }

            for (var index = run.First; index < run.First + run.Count; index++)
            {
                yield return _assigned is null
                    ? EditedEntry.Stored(index)
                    : new EditedEntry(EditedEntry.New, _assigned[index], null);
            }
        }
    }

    private readonly record struct Run(int First, int Count, object? Value);
}
