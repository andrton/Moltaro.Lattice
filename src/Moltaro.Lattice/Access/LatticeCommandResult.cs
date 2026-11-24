namespace Moltaro.Lattice.Access;

/// <summary>
/// The outcome of one command in a <see cref="LatticeUpdate"/>. It becomes available
/// when the update completes successfully on its own or as part of a write batch.
/// </summary>
public readonly struct LatticeCommandResult
{
    /// <summary>
    /// Whether this command changed the entity at its turn. An addition to a Set that
    /// already contained an equal member, or removal of an absent Map key or Set member,
    /// returns false. Every other executed command returns true.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// This result has no successfully completed submission.
    /// </exception>
    public bool Changed => throw new InvalidOperationException("The command has no successfully completed submission.");
}
