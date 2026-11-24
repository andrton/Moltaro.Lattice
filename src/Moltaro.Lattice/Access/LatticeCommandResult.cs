using Moltaro.Lattice.Access.Updates;

namespace Moltaro.Lattice.Access;

/// <summary>
/// The outcome of one command in a <see cref="LatticeUpdate"/>. It becomes available
/// when the update completes successfully on its own or as part of a write batch.
/// </summary>
public readonly struct LatticeCommandResult
{
    private readonly UpdateOutcome? _outcome;
    private readonly int _command;

    internal LatticeCommandResult(UpdateOutcome outcome, int command)
    {
        _outcome = outcome;
        _command = command;
    }

    /// <summary>
    /// Whether this command changed the entity at its turn. An addition to a Set that
    /// already contained an equal member, or removal of an absent Map key or Set member,
    /// returns false. Every other executed command returns true.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// This result has no successfully completed submission.
    /// </exception>
    public bool Changed => (_outcome ?? throw UpdateOutcome.Unavailable()).Changed(_command);
}
