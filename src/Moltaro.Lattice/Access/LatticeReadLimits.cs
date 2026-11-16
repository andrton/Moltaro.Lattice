namespace Moltaro.Lattice.Access;

/// <summary>
/// The limits of one enumeration: how much temporary memory and how much time inside the
/// engine it may use. A member that is <see langword="null"/> takes the value of the
/// database options. Passed to <see cref="ILatticeSet{TEntity}.Enumerate(LatticeReadLimits)"/>,
/// <see cref="ILatticeSet{TEntity}.EnumerateAsync(LatticeSession, LatticeReadLimits, CancellationToken)"/>
/// and their forms for records.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Memory"/> takes the place of <see cref="LatticeDatabaseOptions.ReadMemoryLimit"/>
/// and <see cref="Time"/> that of <see cref="LatticeDatabaseOptions.ReadTimeLimit"/> for one
/// enumeration. A member that is set is the limit of that enumeration, whether it is larger
/// or smaller than the value of the options. A member that is <see langword="null"/> takes
/// the value of the options, and when that is <see langword="null"/> too the enumeration has
/// no such limit. One call therefore cannot lift a limit the options set. The default value
/// sets nothing and leaves both limits to the options. Neither limit reserves anything: a
/// high limit takes no memory of its own, and several enumerations use their limits at the
/// same time.
/// </para>
/// <para>
/// An enumeration that would exceed a limit fails, from the step that would exceed it, with
/// a <see cref="LatticeException"/> of <see cref="LatticeErrorEnum.ReadLimitExceeded"/>
/// whose <see cref="LatticeException.Limit"/> names the limit. The entities it returned
/// before stay valid. It never ends as if it were complete: every further step fails with
/// the same error. A cancellation stays an <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// <b>Not implemented yet.</b> Read limits are not enforced: an enumeration that is given
/// limits throws <see cref="NotSupportedException"/>, and the limits of the options are
/// validated and otherwise not used.
/// </para>
/// </remarks>
/// <param name="Memory">
/// The temporary memory, in bytes, the enumeration may use; <see langword="null"/> for the
/// value of the options.
/// </param>
/// <param name="Time">
/// The time the enumeration may spend inside the engine; <see langword="null"/> for the
/// value of the options.
/// </param>
public readonly record struct LatticeReadLimits(long? Memory = null, TimeSpan? Time = null)
{
    /// <summary>
    /// The temporary memory, in bytes, the enumeration may use; <see langword="null"/> for
    /// <see cref="LatticeDatabaseOptions.ReadMemoryLimit"/>.
    /// </summary>
    /// <remarks>
    /// It counts the temporary memory the engine holds for the enumeration: the identities
    /// it has returned, with the text of their keys, the candidates it took from the index,
    /// and its workspace. That memory grows with the pass. It does not count the copies the
    /// enumeration returns, so an entity larger than the limit is read, nor the memory of
    /// the stored entities and their indexes.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is set and not positive.</exception>
    public long? Memory
    {
        get;
        init => field = CheckedMemory(value);
    } = CheckedMemory(Memory);

    /// <summary>
    /// The time the enumeration may spend inside the engine; <see langword="null"/> for
    /// <see cref="LatticeDatabaseOptions.ReadTimeLimit"/>.
    /// </summary>
    /// <remarks>
    /// It counts the time inside the steps of the enumeration, added up over the pass, a wait
    /// inside the engine included. It does not count the wait for the position of a session,
    /// which <see cref="LatticeSession.SynchronizationTimeout"/> bounds, nor the time between
    /// two steps: a caller that is slow between two entities does not use it up. The engine
    /// checks it at the points of a step where it can stop safely, so a step may end shortly
    /// after the limit is reached.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is set and not positive.</exception>
    public TimeSpan? Time
    {
        get;
        init => field = CheckedTime(value);
    } = CheckedTime(Time);

    // The check is made here, when the value is created or changed with "with", so that no
    // limits that are not positive ever reach a set, whichever implementation stands behind it.
    private static long? CheckedMemory(long? memory)
    {
        if (memory is { } bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes, nameof(Memory));
        }

        return memory;
    }

    private static TimeSpan? CheckedTime(TimeSpan? time)
    {
        if (time is { } span)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(span, TimeSpan.Zero, nameof(Time));
        }

        return time;
    }
}
