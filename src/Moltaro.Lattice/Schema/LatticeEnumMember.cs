namespace Moltaro.Lattice.Schema;

/// <summary>
/// A member of an enum type declared in the schema: one name for one value.
/// </summary>
/// <remarks>
/// <para>
/// Several members may name the same value. They are aliases: an entity stores only the
/// value and does not keep which name was used. One of them is canonical, the name the
/// database uses for that value.
/// </para>
/// <para>
/// In a Flags enum type a member with one bit names an independent flag. A member with
/// several bits names a combination of declared flags and adds no bit of its own.
/// </para>
/// </remarks>
public sealed class LatticeEnumMember
{
    internal LatticeEnumMember(string name, Int128 value, bool isCanonical)
    {
        Name = name;
        Value = value;
        IsCanonical = isCanonical;
    }

    /// <summary>The member name, unique within its enum type.</summary>
    public string Name { get; }

    /// <summary>
    /// The value the member names. It is held as an <see cref="Int128"/>, which represents
    /// every value of the eight integer types exactly, and it lies within the range of the
    /// underlying type of the enum type.
    /// </summary>
    public Int128 Value { get; }

    /// <summary>
    /// Whether this is the canonical name of its value: the only name of a value that has
    /// one, or the one the descriptor marks <c>Canonical</c> among several. Every declared
    /// value has exactly one canonical name.
    /// </summary>
    public bool IsCanonical { get; }
}
