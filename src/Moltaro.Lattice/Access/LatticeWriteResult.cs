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
    /// child returns after that child has applied it. If the child closes after receiving the
    /// master's reply, the call returns the master's result. A write that changed nothing
    /// has a position of its own as well.
    /// </summary>
    public LatticePosition Position { get; }

    /// <summary>
    /// Whether the write changed the database. Deleting an absent key or completing an update
    /// in which every command was unchanged returns <see langword="false"/>. For a batch,
    /// this is whether any of its operations changed the database.
    /// </summary>
    public bool Changed { get; }
}
