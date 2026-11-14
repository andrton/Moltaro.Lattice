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
    /// The instance has stopped because it can no longer guarantee coherent local state.
    /// It serves nothing; close it and create a new instance to recover.
    /// </summary>
    Faulted = 5,

    /// <summary>The instance has been closed and its memory released.</summary>
    Closed = 6,
}
