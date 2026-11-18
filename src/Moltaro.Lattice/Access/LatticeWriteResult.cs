namespace Moltaro.Lattice.Access;

/// <summary>
/// The result of a completed write, or of a completed write batch as a whole.
/// </summary>
public readonly struct LatticeWriteResult
{
    internal LatticeWriteResult(LatticePosition position, bool changed)
    {
        Position = position;
        Changed = changed;
    }

    /// <summary>
    /// The position that includes this write. The master has published it. A write through a
    /// child returns after that child has applied it. If the child is closed first, the call
    /// still returns this result; a closed child serves no read. A write that changed nothing
    /// has a position of its own as well.
    /// </summary>
    public LatticePosition Position { get; }

    /// <summary>
    /// Whether the write changed the database. <see langword="false"/> only when there was
    /// nothing to do: deleting a key that was already absent. For a batch, whether any of
    /// its operations changed the database.
    /// </summary>
    public bool Changed { get; }
}
