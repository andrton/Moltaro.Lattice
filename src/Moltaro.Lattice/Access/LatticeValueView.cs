using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>A field or collection entry read in place for the duration of the entity's viewer.</summary>
/// <remarks>Use the declaration to select an accessor and check <see cref="IsNull"/> before reading a nullable value.</remarks>
public readonly ref struct LatticeValueView
{
    /// <summary>The schema's declaration of this value, including its type and nullability.</summary>
    public LatticeValueDeclaration Declaration => throw new NotSupportedException();

    /// <summary>Whether the value is null.</summary>
    public bool IsNull => throw new NotSupportedException();

    /// <summary>
    /// Reads a fixed-width scalar without allocating, including the integer of an Enum or the
    /// fixed-width key of a Reference.
    /// </summary>
    /// <typeparam name="TValue">The declaration's non-null CLR type.</typeparam>
    /// <returns>The scalar, by value.</returns>
    /// <exception cref="InvalidOperationException">The value is null or does not hold a fixed-width scalar.</exception>
    /// <exception cref="InvalidCastException">The requested type differs from the scalar's declared CLR type.</exception>
    public TValue Get<TValue>() => throw new NotSupportedException();

    /// <summary>
    /// Decodes the stored bytes of a String or a text Reference key using the schema encoding.
    /// The resulting string is owned by the caller.
    /// </summary>
    /// <returns>The decoded text.</returns>
    /// <exception cref="InvalidOperationException">The value is null or does not hold text.</exception>
    public string GetString() => throw new NotSupportedException();

    /// <summary>The total stored byte length of a String, text Reference key or Binary value.</summary>
    /// <exception cref="InvalidOperationException">The value is null or does not hold text or bytes.</exception>
    public int ByteLength => throw new NotSupportedException();

    /// <summary>
    /// The number of contiguous stored parts of a String, text Reference key or Binary value.
    /// An empty value has one empty part.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null or does not hold text or bytes.</exception>
    public int PartCount => throw new NotSupportedException();

    /// <summary>
    /// Borrows one part of the stored bytes of a String, text Reference key or Binary value.
    /// The span is read-only and valid for the duration of the viewer.
    /// </summary>
    /// <param name="part">The zero-based part number.</param>
    /// <returns>The bytes at their original location.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The part number is outside the value's parts.</exception>
    /// <exception cref="InvalidOperationException">The value is null or does not hold text or bytes.</exception>
    public ReadOnlySpan<byte> GetPart(int part) => throw new NotSupportedException();

    /// <summary>Views the fields of an owned value.</summary>
    /// <exception cref="InvalidOperationException">The value is null or is not an Owned value.</exception>
    public LatticeOwnedView Owned => throw new NotSupportedException();

    /// <summary>Views the elements of a List in their stored order.</summary>
    /// <exception cref="InvalidOperationException">The value is null or is not a List.</exception>
    public LatticeListView List => throw new NotSupportedException();

    /// <summary>Views the entries of a Map.</summary>
    /// <exception cref="InvalidOperationException">The value is null or is not a Map.</exception>
    public LatticeMapView Map => throw new NotSupportedException();

    /// <summary>Views the members of a Set.</summary>
    /// <exception cref="InvalidOperationException">The value is null or is not a Set.</exception>
    public LatticeSetView Set => throw new NotSupportedException();
}
