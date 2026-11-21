using Moltaro.Lattice.Access.Values;

namespace Moltaro.Lattice.Access;

/// <summary>
/// One end of a range of index values: the value and whether the value itself belongs to
/// the range.
/// </summary>
/// <remarks>
/// A bound is a small value, made by <see cref="Inclusive{T}"/> or <see cref="Exclusive{T}"/>.
/// Making one allocates nothing and boxes nothing when its value has the .NET type of a scalar
/// field type, or is a <see cref="string"/>, as a value of an <see cref="IndexKey"/> is kept. The
/// default value of <see cref="IndexBound"/>, which neither makes, has no value and tells
/// nothing: a range with it is an <see cref="ArgumentException"/> when the key is used, and
/// reading its <see cref="Value"/> or whether it <see cref="IsInclusive"/> is an
/// <see cref="InvalidOperationException"/>. Its value is accepted and refused as a value of an
/// <see cref="IndexKey"/> is.
/// </remarks>
public readonly struct IndexBound
{
    private readonly bool _isInclusive;

    internal IndexBound(IndexValue held, bool isInclusive)
    {
        Held = held;
        _isInclusive = isInclusive;
    }

    /// <summary>The boundary value, as it was given.</summary>
    /// <remarks>
    /// The value is boxed each time this property is read when it is of a value type. A search
    /// does not read it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The bound is the default of <see cref="IndexBound"/>, which has no value.
    /// </exception>
    public object Value =>
        Held.ToObject() ?? throw new InvalidOperationException("The bound has no value: it is the default of IndexBound, which Inclusive and Exclusive never make.");

    /// <summary>Whether the boundary value itself belongs to the range.</summary>
    /// <exception cref="InvalidOperationException">
    /// The bound is the default of <see cref="IndexBound"/>, which tells nothing.
    /// </exception>
    public bool IsInclusive =>
        Held.IsNull
            ? throw new InvalidOperationException("The bound tells nothing: it is the default of IndexBound, which Inclusive and Exclusive never make.")
            : _isInclusive;

    // The value the bound holds, without a box.
    internal IndexValue Held { get; }

    /// <summary>A bound that includes the value: "from this value on" or "up to and including it".</summary>
    /// <typeparam name="T">The type of the indexed field.</typeparam>
    /// <param name="value">The boundary value.</param>
    public static IndexBound Inclusive<T>(T value)
        where T : notnull =>
        new(IndexValue.Of(value), isInclusive: true);

    /// <summary>A bound that excludes the value: "after this value" or "before it".</summary>
    /// <typeparam name="T">The type of the indexed field.</typeparam>
    /// <param name="value">The boundary value.</param>
    public static IndexBound Exclusive<T>(T value)
        where T : notnull =>
        new(IndexValue.Of(value), isInclusive: false);
}
