namespace Moltaro.Lattice.Access;

/// <summary>
/// One end of a range of index values: the value and whether the value itself belongs to
/// the range.
/// </summary>
public sealed class IndexBound
{
    private IndexBound(object value, bool isInclusive)
    {
        Value = value;
        IsInclusive = isInclusive;
    }

    /// <summary>The boundary value.</summary>
    public object Value { get; }

    /// <summary>Whether the boundary value itself belongs to the range.</summary>
    public bool IsInclusive { get; }

    /// <summary>A bound that includes the value: "from this value on" or "up to and including it".</summary>
    /// <typeparam name="T">The type of the indexed field.</typeparam>
    /// <param name="value">The boundary value.</param>
    public static IndexBound Inclusive<T>(T value)
        where T : notnull =>
        new(value, isInclusive: true);

    /// <summary>A bound that excludes the value: "after this value" or "before it".</summary>
    /// <typeparam name="T">The type of the indexed field.</typeparam>
    /// <param name="value">The boundary value.</param>
    public static IndexBound Exclusive<T>(T value)
        where T : notnull =>
        new(value, isInclusive: false);
}
