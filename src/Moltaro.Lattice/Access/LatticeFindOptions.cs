namespace Moltaro.Lattice.Access;

/// <summary>
/// How an index search is carried out: what it has to be consistent with, in which
/// direction it goes and which part of the matches it returns.
/// </summary>
public sealed class LatticeFindOptions
{
    /// <summary>
    /// The consistency context of the caller. The search waits until this node has applied
    /// the position the session depends on, then moves the session forward to its own
    /// position. <see langword="null"/> to search what this node has now.
    /// </summary>
    public LatticeSession? Session { get; init; }

    /// <summary>
    /// Return the matches in the reverse of the index order. The default is the index order.
    /// </summary>
    public bool Reverse { get; init; }

    /// <summary>The number of matches to skip from the start. The default is none.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Offset
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    }

    /// <summary>
    /// The largest number of matches to return; <see langword="null"/> for all of them.
    /// It limits what is returned, not the work of finding it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int? Limit
    {
        get;
        init
        {
            if (value is { } limit)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
            }

            field = value;
        }
    }
}
