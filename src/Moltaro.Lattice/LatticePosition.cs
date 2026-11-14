using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Moltaro.Lattice;

/// <summary>
/// A point in the life of a database: one database history and everything the master had
/// published in it up to that point. A node that has applied a position has applied all
/// writes before it as well.
/// </summary>
/// <remarks>
/// <para>
/// Every successful operation returns the position its result is based on. Passing that
/// position to a later operation, through a <see cref="LatticeSession"/>, makes the later
/// operation wait until the node that serves it has applied at least that much. This gives
/// read-your-writes across requests, whichever node receives them.
/// </para>
/// <para>
/// A position is not a time, not a revision of an entity and not an identifier of a write.
/// It is opaque: positions are ordered only within one history, and a position of another
/// history is rejected, never compared. Use <see cref="ToString"/> and
/// <see cref="TryParse"/> to carry a position between processes.
/// </para>
/// <para>
/// The default value is not a position of any database; use a nullable
/// <see cref="LatticePosition"/> to express "no position".
/// </para>
/// </remarks>
public readonly struct LatticePosition : IEquatable<LatticePosition>
{
    internal LatticePosition(ulong history, ulong sequence)
    {
        History = history;
        Sequence = sequence;
    }

    /// <summary>
    /// Whether this is a position of some database. <see langword="false"/> only for the
    /// default value.
    /// </summary>
    public bool IsValid => History != 0;

    // Identity of the database history. Never zero for a real position.
    internal ulong History { get; }

    // Number of the last transaction included, within the history. Zero is the empty database.
    internal ulong Sequence { get; }

    /// <summary>Reads a position written by <see cref="ToString"/>.</summary>
    /// <param name="text">The text form of a position.</param>
    /// <param name="position">The position, when the text is valid.</param>
    /// <returns><see langword="true"/> when the text is a position.</returns>
    public static bool TryParse([NotNullWhen(true)] string? text, out LatticePosition position)
    {
        position = default;
        if (text is null)
        {
            return false;
        }

        var separator = text.IndexOf('-');
        if (separator <= 0
            || !ulong.TryParse(text.AsSpan(0, separator), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var history)
            || !ulong.TryParse(text.AsSpan(separator + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var sequence)
            || history == 0)
        {
            return false;
        }

        position = new LatticePosition(history, sequence);
        return true;
    }

    /// <summary>Reads a position written by <see cref="ToString"/>.</summary>
    /// <param name="text">The text form of a position.</param>
    /// <exception cref="FormatException">The text is not a position.</exception>
    public static LatticePosition Parse(string text) =>
        TryParse(text, out var position) ? position : throw new FormatException("The text is not a Lattice position.");

    /// <inheritdoc />
    public bool Equals(LatticePosition other) => History == other.History && Sequence == other.Sequence;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LatticePosition other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(History, Sequence);

    /// <summary>
    /// Returns the text form of the position, suitable for a header, a cookie or a message.
    /// <see cref="TryParse"/> reads it back. Empty for the default value.
    /// </summary>
    public override string ToString() => IsValid ? $"{History:x16}-{Sequence:x}" : "";

    /// <summary>Tests whether two values are the same position of the same history.</summary>
    public static bool operator ==(LatticePosition left, LatticePosition right) => left.Equals(right);

    /// <summary>Tests whether two values differ in history or in position.</summary>
    public static bool operator !=(LatticePosition left, LatticePosition right) => !left.Equals(right);
}
