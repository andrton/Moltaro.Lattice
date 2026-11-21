namespace Moltaro.Lattice.Tests.Indexes.Walk;

// One end of the range of a WalkShape: a value of the type of its field, an int, a long or a string, and
// whether it belongs to the range.
internal sealed record RangeEnd(object Value, bool Inclusive)
{
    public static RangeEnd Including(object value) => new(value, Inclusive: true);

    public static RangeEnd Excluding(object value) => new(value, Inclusive: false);

    public IndexBound Bound() => Value switch
    {
        int number => Inclusive ? IndexBound.Inclusive(number) : IndexBound.Exclusive(number),
        long number => Inclusive ? IndexBound.Inclusive(number) : IndexBound.Exclusive(number),
        string text => Inclusive ? IndexBound.Inclusive(text) : IndexBound.Exclusive(text),
        _ => throw new ArgumentOutOfRangeException(nameof(Value), Value, "No field of Row has the type of this value."),
    };
}
