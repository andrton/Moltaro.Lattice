namespace Moltaro.Lattice.Access;

/// <summary>The result of a protected read and the position the answer is based on.</summary>
/// <typeparam name="TResult">The type of result produced by the viewer.</typeparam>
public readonly struct LatticeViewResult<TResult>
{
    internal LatticeViewResult(bool found, TResult? result, LatticePosition position)
    {
        Found = found;
        Result = result;
        Position = position;
    }

    /// <summary>Whether the entity existed and the viewer was called, even if it returned null.</summary>
    public bool Found { get; }

    /// <summary>The viewer's result, or the default of its type when the entity was absent.</summary>
    public TResult? Result { get; }

    /// <summary>
    /// On a hit, the applied position sampled with the entity's state before the viewer runs.
    /// On a stable miss, the applied position that stayed unchanged through the confirming lookup.
    /// If the read finds an entry and then encounters its tombstone, the position of that delete.
    /// </summary>
    public LatticePosition Position { get; }
}
