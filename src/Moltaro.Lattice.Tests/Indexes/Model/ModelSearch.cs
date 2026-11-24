namespace Moltaro.Lattice.Tests.Indexes.Model;

// One search of an index as the model answers it (IndexModel): the values of its leading parts, none for
// the whole index; a range on the part after them, either end open; and the direction.
internal sealed record ModelSearch(IReadOnlyList<object?> Leading, RangeEnd? From, RangeEnd? To, bool Reverse)
{
    public bool HasRange => From is not null || To is not null;

    public override string ToString()
    {
        var range = HasRange ? $" range {Show(From)} to {Show(To)}" : "";

        return $"({string.Join(", ", Leading.Select(ModelEntry.Show))}){range}{(Reverse ? " in reverse" : "")}";
    }

    private static string Show(RangeEnd? end) =>
        end is null ? "open" : $"{(end.Inclusive ? "inclusive" : "exclusive")} {ModelEntry.Show(end.Value)}";
}
