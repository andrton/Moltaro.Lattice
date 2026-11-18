namespace Moltaro.Lattice;

/// <summary>
/// The consistency context of related operations: the position they depend on, and how
/// long an operation may wait for a node to reach it.
/// </summary>
/// <remarks>
/// <para>
/// Pass the same session to operations that belong together, for example to everything
/// one user does. Each operation first waits until the node that serves it has applied
/// <see cref="MinPosition"/>, then runs, then moves <see cref="MinPosition"/> forward to
/// the position of its own result. A later operation therefore never sees less than an
/// earlier one did, whichever node it runs on.
/// </para>
/// <para>
/// To continue in another process, send <see cref="MinPosition"/> there as text, see
/// <see cref="LatticePosition.ToString"/>, and create a session from it.
/// </para>
/// <para>
/// A session belongs to one database history. Operations on a database with another
/// history fail with <see cref="LatticeErrorEnum.HistoryMismatch"/>; start a new session.
/// A session is safe to use from several threads, but it is not meant to be shared by
/// unrelated users: each would then wait for the others' writes.
/// </para>
/// </remarks>
public sealed class LatticeSession
{
    private readonly Lock _gate = new();
    private LatticePosition? _minPosition;

    /// <summary>Creates a session that does not depend on any position yet.</summary>
    public LatticeSession()
    {
    }

    /// <summary>Creates a session that continues from a position obtained earlier.</summary>
    /// <param name="minPosition">
    /// The position the first operation must wait for; <see langword="null"/> for none.
    /// </param>
    /// <exception cref="ArgumentException">The position is the default value.</exception>
    public LatticeSession(LatticePosition? minPosition)
    {
        if (minPosition is { IsValid: false })
        {
            throw new ArgumentException("The default value is not a position. Pass null for a session without a position.", nameof(minPosition));
        }

        _minPosition = minPosition;
    }

    /// <summary>
    /// The position the next operation waits for; <see langword="null"/> while the session
    /// has no dependency. It only moves forward.
    /// </summary>
    public LatticePosition? MinPosition
    {
        get
        {
            lock (_gate)
            {
                return _minPosition;
            }
        }
    }

    /// <summary>
    /// How long one operation may wait for a node to reach <see cref="MinPosition"/>;
    /// <see langword="null"/> to use <see cref="LatticeDatabaseOptions.SynchronizationTimeout"/>
    /// of the database. The wait is always finite.
    /// </summary>
    /// <remarks>
    /// On a child it also bounds how long a write made with this session waits until the child
    /// has applied it. It bounds only the waits on the node that serves the call: a write
    /// through a child does not carry it to the master, which waits for
    /// <see cref="MinPosition"/> with its own <see cref="LatticeDatabaseOptions.SynchronizationTimeout"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a finite positive time.</exception>
    public TimeSpan? SynchronizationTimeout
    {
        get;
        init
        {
            if (value is { } timeout && !LatticeDatabaseOptions.IsFiniteTimeout(timeout))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The synchronization timeout must be finite and positive.");
            }

            field = value;
        }
    }

    /// <summary>
    /// Moves <see cref="MinPosition"/> forward to the given position. A position that is
    /// not ahead of the current one changes nothing, so results that arrive out of order
    /// never move the session back. Operations that take the session call this themselves.
    /// </summary>
    /// <param name="position">A position returned by an operation.</param>
    /// <exception cref="ArgumentException">The position is the default value.</exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.HistoryMismatch"/>: the position belongs to another
    /// database history than the session.
    /// </exception>
    public void Advance(LatticePosition position)
    {
        if (!position.IsValid)
        {
            throw new ArgumentException("The default value is not a position.", nameof(position));
        }

        lock (_gate)
        {
            if (_minPosition is not { } current)
            {
                _minPosition = position;
                return;
            }

            if (current.History != position.History)
            {
                throw new LatticeException(
                    LatticeErrorEnum.HistoryMismatch,
                    "The position belongs to another database history than the session. Start a new session.");
            }

            if (position.Sequence > current.Sequence)
            {
                _minPosition = position;
            }
        }
    }
}
