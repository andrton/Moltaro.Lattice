namespace Moltaro.Lattice;

/// <summary>
/// Lifecycle state of a database instance. Reads and writes are served only in
/// <see cref="Ready"/>.
/// </summary>
public enum LatticeDatabaseStateEnum
{
    /// <summary>The instance has been created and not opened yet.</summary>
    Created = 0,

    /// <summary>A child is establishing the connection to its master.</summary>
    Connecting = 1,

    /// <summary>
    /// The database state is being loaded and local indexes are being built: from durable
    /// storage on a master, from the master on a child.
    /// </summary>
    Loading = 2,

    /// <summary>
    /// The loaded state is being brought forward: a master replays its committed journal
    /// tail, a child applies changes up to its readiness position.
    /// </summary>
    CatchingUp = 3,

    /// <summary>The complete database and its indexes are available.</summary>
    Ready = 4,

    /// <summary>
    /// The instance has stopped because it can no longer guarantee coherent local state, for
    /// example because the publication of a write failed. It serves nothing: every operation that
    /// begins in this state, a read included, reports <see cref="LatticeErrorEnum.Unavailable"/>,
    /// and <see cref="ILatticeDatabase.CloseAsync"/> works. Close it and create a new instance to
    /// recover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No read returns a mixture of two states of an entity, and none is left waiting. A read that
    /// overlaps the publication that failed returns the state before the write, the whole state of
    /// the write, or <see cref="LatticeErrorEnum.Unavailable"/>; a read that begins after the
    /// instance is Faulted reports <see cref="LatticeErrorEnum.Unavailable"/>.
    /// </para>
    /// <para>
    /// The writes in progress end as follows. A write that was published before the failure
    /// returns its result. The write whose publication failed, and the writes the database had
    /// taken for storage together with it and after it, fail with
    /// <see cref="LatticeErrorEnum.IndeterminateOutcome"/>: storage took them, and the instance
    /// cannot show them. The first of them carries the cause of the failure as its
    /// <see cref="Exception.InnerException"/>. Writes that had not been handed to storage yet fail
    /// with <see cref="LatticeErrorEnum.Unavailable"/>: they did not run, and they hold nothing.
    /// </para>
    /// <para>
    /// A MemoryOnly instance that faulted has lost its data.
    /// </para>
    /// </remarks>
    Faulted = 5,

    /// <summary>The instance has been closed and its memory released.</summary>
    Closed = 6,
}
