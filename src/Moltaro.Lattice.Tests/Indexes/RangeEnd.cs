namespace Moltaro.Lattice.Tests.Indexes;

// One end of a range in a test of an index: a value of a scalar type, given as its own .NET type when the
// bound is made (TypedValues), and whether it belongs to the range.
internal sealed record RangeEnd(object Value, bool Inclusive)
{
    public static RangeEnd Including(object value) => new(value, Inclusive: true);

    public static RangeEnd Excluding(object value) => new(value, Inclusive: false);

    public IndexBound Bound() => TypedValues.Bound(Value, Inclusive);
}
