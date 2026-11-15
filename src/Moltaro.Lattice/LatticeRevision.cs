namespace Moltaro.Lattice;

/// <summary>
/// An opaque token identifying one state of a stored entity, including everything the
/// entity owns. Every committed change of the entity produces a new revision.
/// </summary>
/// <remarks>
/// <para>
/// A revision identifies one state of one entity. Two revisions of the same entity can be
/// compared for equality, and nothing else may be concluded from a revision: it has no
/// order, it cannot be turned into a position, and the revision of one entity means
/// nothing for another.
/// </para>
/// <para>
/// A revision is read together with an entity and can be required by a later write to
/// detect intervening changes. It has no numerical meaning. A token never matches another
/// entity, an entity recreated under the same key or another database history. Two
/// different tokens do not prove that the entity contents differ. The default value
/// matches no stored entity.
/// </para>
/// </remarks>
public readonly struct LatticeRevision : IEquatable<LatticeRevision>
{
    private readonly ulong _high;
    private readonly ulong _low;

    internal LatticeRevision(ulong high, ulong low)
    {
        _high = high;
        _low = low;
    }

    /// <inheritdoc />
    public bool Equals(LatticeRevision other) => _high == other._high && _low == other._low;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LatticeRevision other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_high, _low);

    /// <summary>Returns a diagnostic representation. Its format is not a contract.</summary>
    public override string ToString() => $"{_high:x16}{_low:x16}";

    /// <summary>Tests whether two tokens identify the same entity state.</summary>
    public static bool operator ==(LatticeRevision left, LatticeRevision right) => left.Equals(right);

    /// <summary>Tests whether two tokens identify different entity states.</summary>
    public static bool operator !=(LatticeRevision left, LatticeRevision right) => !left.Equals(right);
}
