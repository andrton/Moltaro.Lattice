using System.Collections;
using Moltaro.Lattice.Access.Updates;

namespace Moltaro.Lattice.Storage.Roots.Edits;

// Runs of original entries or single new elements. Checking a position and
// editing a run does not copy the list of unchanged elements into managed memory.
internal sealed class EditedList
{
    private readonly IList? _assigned;
    private readonly List<Run> _runs;

    public EditedList(int count, IList? assigned)
    {
        Count = count;
        _assigned = assigned;
        _runs = count == 0 ? [] : [new Run(0, count, null)];
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
            _runs.Insert(place, new Run(EditedEntry.New, 1, command.First));
        }
        else
        {
            var run = _runs[place];
            _runs.RemoveAt(place);
            if (within != 0)
            {
                _runs.Insert(place++, run with { Count = within });
            }

            if (command.Kind != UpdateCommandKindEnum.ListRemove)
            {
                _runs.Insert(place++, new Run(EditedEntry.New, 1, command.First));
            }

            var consumed = within + (deletes ? 1 : 0);
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
