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
/// Values are given in the order of the index fields and have the types of those fields; an
/// integer of another width is accepted when it fits. A key that an index cannot serve,
/// such as a value for the second field without one for the first, is rejected when it is
/// used; nothing falls back to reading every entity.
/// </para>
/// </remarks>
public sealed class IndexKey
{
    private IndexKey(object?[] values, IndexBound? from, IndexBound? to)
    {
        Values = values;
        From = from;
        To = to;
    }

    /// <summary>The whole index, in its order.</summary>
    public static IndexKey All { get; } = new([], null, null);

    // Values of the leading index fields that must be equal.
    internal object?[] Values { get; }

    // Range on the field after the equal ones; either end may be open.
    internal IndexBound? From { get; }

    internal IndexBound? To { get; }

    /// <summary>Entities whose first index field has this value.</summary>
    /// <typeparam name="T1">The type of the first index field.</typeparam>
    /// <param name="value1">The value of the first index field.</param>
    public static IndexKey Equal<T1>(T1 value1) => new([value1], null, null);

    /// <summary>Entities whose first two index fields have these values.</summary>
    /// <typeparam name="T1">The type of the first index field.</typeparam>
    /// <typeparam name="T2">The type of the second index field.</typeparam>
    /// <param name="value1">The value of the first index field.</param>
    /// <param name="value2">The value of the second index field.</param>
    public static IndexKey Equal<T1, T2>(T1 value1, T2 value2) => new([value1, value2], null, null);

    /// <summary>Entities whose first three index fields have these values.</summary>
    /// <typeparam name="T1">The type of the first index field.</typeparam>
    /// <typeparam name="T2">The type of the second index field.</typeparam>
    /// <typeparam name="T3">The type of the third index field.</typeparam>
    /// <param name="value1">The value of the first index field.</param>
    /// <param name="value2">The value of the second index field.</param>
    /// <param name="value3">The value of the third index field.</param>
    public static IndexKey Equal<T1, T2, T3>(T1 value1, T2 value2, T3 value3) => new([value1, value2, value3], null, null);

    /// <summary>Entities whose first four index fields have these values.</summary>
    /// <typeparam name="T1">The type of the first index field.</typeparam>
    /// <typeparam name="T2">The type of the second index field.</typeparam>
    /// <typeparam name="T3">The type of the third index field.</typeparam>
    /// <typeparam name="T4">The type of the fourth index field.</typeparam>
    /// <param name="value1">The value of the first index field.</param>
    /// <param name="value2">The value of the second index field.</param>
    /// <param name="value3">The value of the third index field.</param>
    /// <param name="value4">The value of the fourth index field.</param>
    public static IndexKey Equal<T1, T2, T3, T4>(T1 value1, T2 value2, T3 value3, T4 value4) => new([value1, value2, value3, value4], null, null);

    /// <summary>
    /// Narrows the key to a range of values of the next index field: the one after the
    /// fields that already have a value. Entities without a value in that field are not in
    /// any range.
    /// </summary>
    /// <param name="from">Where the range starts; <see langword="null"/> for no lower end.</param>
    /// <param name="to">Where the range ends; <see langword="null"/> for no upper end.</param>
    /// <exception cref="ArgumentException">Both ends are <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The key already has a range.</exception>
    public IndexKey Range(IndexBound? from, IndexBound? to)
    {
        if (from is null && to is null)
        {
            throw new ArgumentException("A range needs at least one end.");
        }

        if (From is not null || To is not null)
        {
            throw new InvalidOperationException("The key already has a range.");
        }

        return new IndexKey(Values, from, to);
    }
}
