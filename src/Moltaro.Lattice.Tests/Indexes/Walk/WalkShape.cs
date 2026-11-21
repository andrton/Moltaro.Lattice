namespace Moltaro.Lattice.Tests.Indexes.Walk;

// One search of a walk test: the index, by the number of its parts (WalkSchema.Indexes); the leading
// parts that get a value, with the values of the row given; a range on the part after them, either end
// open; and the direction. Key makes the IndexKey of it, and WalkOracle its answer.
internal sealed record WalkShape(int Parts, int Leading, Row? Values, RangeEnd? From, RangeEnd? To, bool Reverse)
{
    public string Index => WalkSchema.Indexes[Parts - 1];

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
        var values = Values is null ? "" : string.Join(", ", Enumerable.Range(0, Leading).Select(part => Show(WalkOracle.PartOf(Values, part))));
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
