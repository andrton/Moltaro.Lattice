using Moltaro.Lattice.Access.Values;

namespace Moltaro.Lattice.Access;

/// <summary>
/// What to look for in an index: values for its leading fields, and optionally a range of
/// values for the field after them.
/// </summary>
/// <remarks>
/// <para>
/// For an index on (Section, Name):
/// </para>
/// <list type="bullet">
/// <item><c>IndexKey.Equal("mail", "sender")</c> — the entities with exactly this section and name.</item>
/// <item><c>IndexKey.Equal("mail")</c> — all entities of the section, ordered by name.</item>
/// <item><c>IndexKey.Equal("mail").Range(IndexBound.Inclusive("a"), IndexBound.Exclusive("n"))</c> —
/// those of them whose name is from "a" up to, but not including, "n".</item>
/// <item><c>IndexKey.All</c> — the whole index in its order.</item>
/// </list>
/// <para>
/// Values are given in the order of the index fields and have the types of those fields. A
/// number of another type is accepted when it is the same number in the type of the field: an
/// integer of another width that fits; any integer for a Decimal field; an integer that a
/// Float32 or a Float64 field holds exactly; a Float32 for a Float64 field; a Float64 for a
/// Float32 field when it converts back to itself, with NaN, an infinity and the sign of zero
/// kept. Nothing else is converted: a Decimal or a floating-point value is not accepted for an
/// integer field, 42.0 included, nor a Decimal for a floating-point field, nor the reverse. A
/// value for an Enum field is its integer, and it has to be one the enum type allows; a .NET
/// enum value is not accepted. A String value that is not well-formed
/// text, with a surrogate without its pair, is an <see cref="ArgumentException"/> when the key
/// is used, as a value of another type is. A key that an index cannot serve, such as a value
/// for the second field without one for the first, is rejected when it is used; nothing falls
/// back to reading every entity.
/// </para>
/// <para>
/// A value may be <see langword="null"/> exactly where a value of that part of the index can
/// be null: the indexed field is nullable, or a step of the path of the part is a nullable
/// owned value, which is absent in such an entity, see <see cref="Schema.LatticeIndexPart"/>.
/// <see langword="null"/> for any other part is an <see cref="ArgumentException"/>. A
/// <see langword="null"/> that has a type, a <see cref="Nullable{T}"/> without a value or a null
/// <see cref="string"/>, is accepted only where a value of that type would be. A null given as
/// any other class is refused, as a null of a type no field has.
/// </para>
/// <para>
/// A key is a small value. Making one allocates nothing and boxes none of its values when each
/// has the .NET type of a scalar field type, or is a <see cref="Nullable{T}"/> of one, or a
/// <see cref="string"/>: the values are kept in the key as they were given, and a value given as
/// an <see cref="object"/> is unboxed, not boxed again. A value of any other type is kept as an
/// object, to be refused when the key is used. A key can be kept and used for any number of
/// searches, from any thread; a copy of a key is the same key.
/// </para>
/// <para>
/// A key is made by <see cref="All"/>, an <c>Equal</c> or <see cref="Range"/>. The default value
/// of <see cref="IndexKey"/>, which none of them makes, is no key, and is an error wherever it is
/// used, as the default of <see cref="IndexBound"/> is: given to a search, <c>Find</c> or
/// <c>FindAsync</c> of <see cref="ILatticeIndex{TEntity}"/>, it is an
/// <see cref="ArgumentException"/>; its one member, <see cref="Range"/>, is an
/// <see cref="InvalidOperationException"/>. <see cref="All"/> is the key of every entry of an
/// index, and is not the default. What every struct inherits from <see cref="object"/>,
/// <c>Equals</c>, <c>GetHashCode</c> and <c>ToString</c>, does not read the key and refuses
/// nothing.
/// </para>
/// </remarks>
public readonly struct IndexKey
{
    // The values one Equal takes at most, in the order of the index fields.
    private const int MostValues = 4;

    // How many of the values below the key has: one for each leading field, at most MostValues.
    private readonly int _count;

    // Whether a factory made the key; false only for the default value, which is no key.
    private readonly bool _made;

    private readonly IndexValue _value1;

    private readonly IndexValue _value2;

    private readonly IndexValue _value3;

    private readonly IndexValue _value4;

    internal IndexKey(int count, IndexValue value1, IndexValue value2, IndexValue value3, IndexValue value4, IndexBound? from, IndexBound? to)
    {
        _count = count;
        _made = true;
        _value1 = value1;
        _value2 = value2;
        _value3 = value3;
        _value4 = value4;
        From = from;
        To = to;
    }

    /// <summary>
    /// The whole index, in its order: the key of every entry. It is not the default value of
    /// <see cref="IndexKey"/>, which is no key.
    /// </summary>
    public static IndexKey All => new(0, default, default, default, default, null, null);

    // Whether this is the default value, which no factory makes and which is no key.
    internal bool IsDefault => !_made;

    // The values of the leading index fields that must be equal: as many as the key has.
    internal int Count => _count;

    // Range on the field after the equal ones; either end may be open.
    internal IndexBound? From { get; }

    internal IndexBound? To { get; }

    /// <summary>Entities whose first index field has this value.</summary>
    /// <typeparam name="T1">The type of the value of the first index field: the type of that field, or one the remarks accept for it.</typeparam>
    /// <param name="value1">The value of the first index field.</param>
    public static IndexKey Equal<T1>(T1 value1) =>
        new(1, IndexValue.Of(value1), default, default, default, null, null);

    /// <summary>Entities whose first two index fields have these values.</summary>
    /// <typeparam name="T1">The type of the value of the first index field: the type of that field, or one the remarks accept for it.</typeparam>
    /// <typeparam name="T2">The type of the value of the second index field: the type of that field, or one the remarks accept for it.</typeparam>
    /// <param name="value1">The value of the first index field.</param>
    /// <param name="value2">The value of the second index field.</param>
    public static IndexKey Equal<T1, T2>(T1 value1, T2 value2) =>
        new(2, IndexValue.Of(value1), IndexValue.Of(value2), default, default, null, null);

    /// <summary>Entities whose first three index fields have these values.</summary>
    /// <typeparam name="T1">The type of the value of the first index field: the type of that field, or one the remarks accept for it.</typeparam>
    /// <typeparam name="T2">The type of the value of the second index field: the type of that field, or one the remarks accept for it.</typeparam>
    /// <typeparam name="T3">The type of the value of the third index field: the type of that field, or one the remarks accept for it.</typeparam>
    /// <param name="value1">The value of the first index field.</param>
    /// <param name="value2">The value of the second index field.</param>
    /// <param name="value3">The value of the third index field.</param>
    public static IndexKey Equal<T1, T2, T3>(T1 value1, T2 value2, T3 value3) =>
        new(3, IndexValue.Of(value1), IndexValue.Of(value2), IndexValue.Of(value3), default, null, null);

    /// <summary>Entities whose first four index fields have these values.</summary>
    /// <typeparam name="T1">The type of the value of the first index field: the type of that field, or one the remarks accept for it.</typeparam>
    /// <typeparam name="T2">The type of the value of the second index field: the type of that field, or one the remarks accept for it.</typeparam>
    /// <typeparam name="T3">The type of the value of the third index field: the type of that field, or one the remarks accept for it.</typeparam>
    /// <typeparam name="T4">The type of the value of the fourth index field: the type of that field, or one the remarks accept for it.</typeparam>
    /// <param name="value1">The value of the first index field.</param>
    /// <param name="value2">The value of the second index field.</param>
    /// <param name="value3">The value of the third index field.</param>
    /// <param name="value4">The value of the fourth index field.</param>
    public static IndexKey Equal<T1, T2, T3, T4>(T1 value1, T2 value2, T3 value3, T4 value4) =>
        new(MostValues, IndexValue.Of(value1), IndexValue.Of(value2), IndexValue.Of(value3), IndexValue.Of(value4), null, null);

    /// <summary>
    /// Narrows the key to a range of values of the next index field: the one after the
    /// fields that already have a value. Entities without a value in that field are not in
    /// any range.
    /// </summary>
    /// <remarks>
    /// The key it is called on does not change: the range is in the key it returns.
    /// </remarks>
    /// <param name="from">Where the range starts; <see langword="null"/> for no lower end.</param>
    /// <param name="to">Where the range ends; <see langword="null"/> for no upper end.</param>
    /// <exception cref="InvalidOperationException">
    /// The key already has a range, or it is the default of <see cref="IndexKey"/>, which is no key.
    /// </exception>
    /// <exception cref="ArgumentException">Both ends are <see langword="null"/>.</exception>
    public IndexKey Range(IndexBound? from, IndexBound? to)
    {
        if (!_made)
        {
            throw new InvalidOperationException("The key is the default of IndexKey, which is no key: make one with IndexKey.All or IndexKey.Equal.");
        }

        if (from is null && to is null)
        {
            throw new ArgumentException("A range needs at least one end.");
        }

        if (From is not null || To is not null)
        {
            throw new InvalidOperationException("The key already has a range.");
        }

        return new IndexKey(_count, _value1, _value2, _value3, _value4, from, to);
    }

    // The value of a leading index field, 0 to Count less one, as the key holds it.
    internal IndexValue ValueAt(int part) => part switch
    {
        0 when _count > 0 => _value1,
        1 when _count > 1 => _value2,
        2 when _count > 2 => _value3,
        3 when _count > 3 => _value4,
        _ => throw new ArgumentOutOfRangeException(nameof(part), part, $"The key has {_count} values."),
    };
}
