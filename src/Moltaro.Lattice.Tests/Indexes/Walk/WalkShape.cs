using Moltaro.Lattice.Tests.Indexes.Model;

namespace Moltaro.Lattice.Tests.Indexes.Walk;

// One search of a walk test: the index, by the number of its parts (WalkSchema.Indexes); the leading
// parts that get a value, with the values of the row given; a range on the part after them, either end
// open; and the direction. Key makes the IndexKey of it, Search the search of the model, and Model the
// model of its index, whose answer is the one to hold.
internal sealed record WalkShape(int Parts, int Leading, Row? Values, RangeEnd? From, RangeEnd? To, bool Reverse)
{
    public string Index => WalkSchema.Indexes[Parts - 1];

    // The search as the model answers it.
    public ModelSearch Search => new([.. (Values?.Key ?? []).Take(Leading)], From, To, Reverse);

    // The model of the index: A and C by value, B Ordinal, D without case (WalkSchema).
    public IndexModel Model => new([.. new[] { IndexModel.ByValue, IndexModel.Ordinal, IndexModel.ByValue, IndexModel.WithoutCase }.Take(Parts)]);

    // The answer of the model over the rows, in the order of the index or in its exact reverse.
    public List<ModelEntry> Answer(IEnumerable<Row> rows) => Model.Answer(rows.Select(row => row.Entry()), Search);

    // The key of the search, its values in the types of the fields A to D.
    public IndexKey Key()
    {
        var values = Values ?? new Row();
        var key = Leading switch
        {
            0 => IndexKey.All,
            1 => IndexKey.Equal(values.A),
            2 => IndexKey.Equal(values.A, values.B),
            3 => IndexKey.Equal(values.A, values.B, values.C),
            _ => IndexKey.Equal(values.A, values.B, values.C, values.D),
        };

        return From is null && To is null ? key : key.Range(From?.Bound(), To?.Bound());
    }

    public override string ToString()
    {
        var values = Values is null ? "" : string.Join(", ", Enumerable.Range(0, Leading).Select(part => Show(Values.Key[part])));
        var range = From is null && To is null ? "" : $" range {Show(From)} to {Show(To)}";

        return $"{Index}({values}){range}{(Reverse ? " in reverse" : "")}";
    }

    private static string Show(object? value) => value switch
    {
        null => "null",
        RangeEnd end => $"{(end.Inclusive ? "inclusive" : "exclusive")} {Show(end.Value)}",
        string text when text.Length > 12 => $"\"{text[..12]}…\"",
        string text => $"\"{text}\"",
        _ => value.ToString()!,
    };
}
