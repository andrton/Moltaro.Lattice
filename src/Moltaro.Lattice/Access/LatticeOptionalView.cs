namespace Moltaro.Lattice.Access;

/// <summary>A nullable owned value or collection borrowed for the duration of a protected read.</summary>
/// <remarks>The default wrapper is absent. A present empty collection has a value.</remarks>
/// <typeparam name="TView">The borrowed view of a present value.</typeparam>
public readonly ref struct LatticeOptionalView<TView>
    where TView : allows ref struct
{
    private readonly TView _value;

    /// <summary>Wraps a present value for the lifetime of the borrowed view.</summary>
    /// <param name="value">The borrowed view.</param>
    public LatticeOptionalView(TView value)
    {
        _value = value;
        HasValue = true;
    }

    /// <summary>Whether the field has a value.</summary>
    public bool HasValue { get; }

    /// <summary>The borrowed view of the value, when present.</summary>
    /// <exception cref="InvalidOperationException">The field is absent.</exception>
    public TView Value => HasValue ? _value : throw new InvalidOperationException("The view has no value.");
}
