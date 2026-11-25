using Moltaro.Lattice.Access.Views;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>A field or collection entry read in place for the duration of the entity's viewer.</summary>
/// <remarks>Use the declaration to select an accessor and check <see cref="IsNull"/> before reading a nullable value.</remarks>
public readonly ref struct LatticeValueView
{
    private readonly IViewStorage _storage;
    private readonly ViewRecord _row;
    private readonly int _position;

    internal LatticeValueView(IViewStorage storage, ViewRecord row, int position, LatticeValueDeclaration declaration)
    {
        _storage = storage;
        _row = row;
        _position = position;
        Declaration = declaration;
    }

    /// <summary>The schema's declaration of this value, including its type and nullability.</summary>
    public LatticeValueDeclaration Declaration { get; }

    /// <summary>Whether the value is null.</summary>
    public bool IsNull => _storage.IsNull(_row, _position);

    /// <summary>
    /// Reads a fixed-width scalar without allocating, including the integer of an Enum or the
    /// fixed-width key of a Reference.
    /// </summary>
    /// <typeparam name="TValue">The declaration's non-null CLR type.</typeparam>
    /// <returns>The scalar, by value.</returns>
    /// <exception cref="InvalidOperationException">The value is null or does not hold a fixed-width scalar.</exception>
    /// <exception cref="InvalidCastException">The requested type differs from the scalar's declared CLR type.</exception>
    public TValue Get<TValue>()
    {
        if (Declaration.ScalarType is LatticeFieldTypeEnum.String or LatticeFieldTypeEnum.Binary or LatticeFieldTypeEnum.Owned
            or LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Map or LatticeFieldTypeEnum.Set)
        {
            throw WrongKind("a fixed-width scalar");
        }

        if (typeof(TValue) != Declaration.ValueType)
        {
            throw new InvalidCastException($"The declared scalar uses {Declaration.ValueType.Name}, not {typeof(TValue).Name}.");
        }

        RequireValue();
        return _storage.ReadScalar<TValue>(_row, _position);
    }

    /// <summary>
    /// Decodes the stored bytes of a String or a text Reference key using the schema encoding.
    /// The resulting string is owned by the caller.
    /// </summary>
    /// <returns>The decoded text.</returns>
    /// <exception cref="InvalidOperationException">The value is null or does not hold text.</exception>
    public string GetString()
    {
        if (Declaration.ScalarType != LatticeFieldTypeEnum.String)
        {
            throw WrongKind("text");
        }

        RequireValue();
        return _storage.ReadString(_row, _position);
    }

    /// <summary>The total stored byte length of a String, text Reference key or Binary value.</summary>
    /// <exception cref="InvalidOperationException">The value is null or does not hold text or bytes.</exception>
    public int ByteLength => ByteShape().Length;

    /// <summary>
    /// The number of contiguous stored parts of a String, text Reference key or Binary value.
    /// An empty value has one empty part.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null or does not hold text or bytes.</exception>
    public int PartCount => ByteShape().Parts;

    /// <summary>
    /// Borrows one part of the stored bytes of a String, text Reference key or Binary value.
    /// The span is read-only and valid for the duration of the viewer.
    /// </summary>
    /// <param name="part">The zero-based part number.</param>
    /// <returns>The bytes at their original location.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The part number is outside the value's parts.</exception>
    /// <exception cref="InvalidOperationException">The value is null or does not hold text or bytes.</exception>
    public ReadOnlySpan<byte> GetPart(int part)
    {
        var parts = ByteShape().Parts;
        ArgumentOutOfRangeException.ThrowIfNegative(part);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(part, parts);
        return _storage.GetPart(_row, _position, part);
    }

    /// <summary>Views the fields of an owned value.</summary>
    /// <exception cref="InvalidOperationException">The value is null or is not an Owned value.</exception>
    public LatticeOwnedView Owned
    {
        get
        {
            RequireKind(LatticeFieldTypeEnum.Owned);
            return new LatticeOwnedView(_storage, _row, _position, Declaration.OwnedType!);
        }
    }

    /// <summary>Views the elements of a List in their stored order.</summary>
    /// <exception cref="InvalidOperationException">The value is null or is not a List.</exception>
    public LatticeListView List
    {
        get
        {
            RequireKind(LatticeFieldTypeEnum.List);
            return new LatticeListView(_storage, _storage.Collection(_row, _position), Declaration.Element!);
        }
    }

    /// <summary>Views the entries of a Map.</summary>
    /// <exception cref="InvalidOperationException">The value is null or is not a Map.</exception>
    public LatticeMapView Map
    {
        get
        {
            RequireKind(LatticeFieldTypeEnum.Map);
            return new LatticeMapView(_storage, _storage.Collection(_row, _position), Declaration);
        }
    }

    /// <summary>Views the members of a Set.</summary>
    /// <exception cref="InvalidOperationException">The value is null or is not a Set.</exception>
    public LatticeSetView Set
    {
        get
        {
            RequireKind(LatticeFieldTypeEnum.Set);
            return new LatticeSetView(_storage, _storage.Collection(_row, _position), Declaration.Element!);
        }
    }

    private (int Length, int Parts) ByteShape()
    {
        if (Declaration.ScalarType is not (LatticeFieldTypeEnum.String or LatticeFieldTypeEnum.Binary))
        {
            throw WrongKind("text or bytes");
        }

        RequireValue();
        return _storage.ByteShape(_row, _position);
    }

    private void RequireKind(LatticeFieldTypeEnum expected)
    {
        if (Declaration.Type != expected)
        {
            throw WrongKind(expected.ToString());
        }

        RequireValue();
    }

    private void RequireValue()
    {
        if (IsNull)
        {
            throw new InvalidOperationException("A null view has no value to read.");
        }
    }

    private InvalidOperationException WrongKind(string expected) =>
        new($"The declared {Declaration.Type} value does not hold {expected}.");
}
