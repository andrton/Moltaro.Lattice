namespace Moltaro.Lattice;

/// <summary>
/// A failure reported by the database. <see cref="Error"/> tells the failures apart.
/// </summary>
public class LatticeException : Exception
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="error">The reason of the failure.</param>
    /// <param name="message">A description of the failure.</param>
    /// <param name="innerException">The underlying exception, if any.</param>
    public LatticeException(LatticeErrorEnum error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    /// <summary>The reason of the failure.</summary>
    public LatticeErrorEnum Error { get; }

    /// <summary>
    /// Set when a write through a child was committed by the master but the child did not
    /// apply it in time: the position that includes the write. The write is not rolled back
    /// and must not be submitted again as a new write. An operation that requires this
    /// position, through a <see cref="LatticeSession"/>, sees the write.
    /// </summary>
    public LatticePosition? CommittedPosition { get; init; }

    /// <summary>
    /// Set for <see cref="LatticeErrorEnum.UniqueConstraintViolation"/>: the name of the
    /// unique index the write would have violated.
    /// </summary>
    public string? IndexName { get; init; }

    /// <summary>
    /// Set when a write batch was refused because of one of its operations: the number of
    /// that operation in the order the operations were added, starting at zero.
    /// </summary>
    /// <remarks>
    /// A batch refused for its operations names one of them, in two phases: first each operation
    /// is checked against the state before the batch, in the order they were added, and the first
    /// that fails a condition of its own is named; when none does, the operation with the lowest
    /// number that would give a value of a unique index a second owner on the state the batch
    /// would leave, with the index declared first when it does so in several. A refusal for memory
    /// names no operation.
    /// </remarks>
    public int? OperationIndex { get; init; }

    /// <summary>
    /// Set for <see cref="LatticeErrorEnum.ReadLimitExceeded"/>, the limit the read would have
    /// exceeded, and for <see cref="LatticeErrorEnum.WriteRejected"/>, the limit that refused
    /// the write.
    /// </summary>
    public LatticeLimitEnum? Limit { get; init; }
}
