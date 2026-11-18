using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice;

/// <summary>
/// Configuration of one database instance. One instance opens one database; the
/// configuration is fixed for the instance lifetime.
/// </summary>
public sealed class LatticeDatabaseOptions
{
    /// <summary>
    /// Role of the instance. The default is <see cref="LatticeRoleEnum.Master"/>.
    /// </summary>
    public LatticeRoleEnum Role { get; init; } = LatticeRoleEnum.Master;

    /// <summary>
    /// Persistence mode. The default is <see cref="LatticePersistenceModeEnum.Durable"/>;
    /// <see cref="LatticePersistenceModeEnum.MemoryOnly"/> must be selected explicitly.
    /// A child must select <see cref="LatticePersistenceModeEnum.MemoryOnly"/>.
    /// </summary>
    public LatticePersistenceModeEnum PersistenceMode { get; init; } = LatticePersistenceModeEnum.Durable;

    /// <summary>
    /// The compiled schema of the database, see <see cref="LatticeSchema"/>. A MemoryOnly master
    /// requires it. A Durable master requires it when its storage holds no database yet. A child
    /// receives the schema from its master and must not set it.
    /// </summary>
    public LatticeSchema? Schema { get; init; }

    /// <summary>
    /// Connection string of the durable storage; PostgreSQL is the first supported backend.
    /// Required for a Durable master and not allowed otherwise.
    /// </summary>
    public string? StorageConnectionString { get; init; }

    /// <summary>
    /// Address at which a master accepts its children. Optional: a master without it has no
    /// children and serves only its own process. Not allowed for a child.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An address has the scheme <c>lattice</c>, a host and a TCP port, and nothing else:
    /// <c>lattice://host:7400</c>. <see cref="Validate"/> refuses an address with another scheme,
    /// without a host or a port, or with a path with <see cref="LatticeErrorEnum.InvalidConfiguration"/>.
    /// The master listens over TCP on the network interface that the host names: an IP address,
    /// or a name that the system resolves, whose first address is taken. Port 0 lets the system
    /// choose a free port; the open master reports the address it listens at, with that port, in
    /// <see cref="ILatticeDatabase.ListenUri"/>, and that is the address its children connect to.
    /// A master that cannot listen at its address, because the address is already in use for
    /// example, fails its open with <see cref="LatticeErrorEnum.InvalidConfiguration"/>, whose
    /// message gives the reason the system reports, and the instance is closed.
    /// </para>
    /// <para>
    /// The engine authenticates no peer at this address: whoever reaches it can load the whole
    /// database. Keep it reachable only from the nodes of the database.
    /// </para>
    /// </remarks>
    public Uri? ListenUri { get; init; }

    /// <summary>
    /// Address of the master: the address the open master reports in
    /// <see cref="ILatticeDatabase.ListenUri"/>. Required for a child and not allowed for a master.
    /// </summary>
    /// <remarks>
    /// It has the form of <see cref="ListenUri"/>, with the port at which the master listens: the
    /// master's own <see cref="ListenUri"/>, unless that names port 0. <see cref="Validate"/>
    /// refuses another form, and port 0, with <see cref="LatticeErrorEnum.InvalidConfiguration"/>.
    /// The child connects to its master at this address over TCP, loads the whole database, as
    /// <see cref="ILatticeDatabase.OpenAsync"/> says, and then follows the writes of the master.
    /// </remarks>
    public Uri? MasterUri { get; init; }

    /// <summary>
    /// How long a child waits for its master while it opens: to reach the master and be accepted
    /// by it, and afterwards for the next part of the database to arrive. A load that keeps
    /// arriving is not ended by this timeout, however long it takes; the cancellation token of the
    /// open call bounds the whole open. 30 seconds by default; finite and positive. A master gives
    /// a node that connects to it the same time to introduce itself, and disconnects one that
    /// does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While the master cannot be reached, the child keeps trying until the time is up, so a child
    /// may be started before its master. When the master cannot be reached, or nothing arrives
    /// from it, for this time, the open fails with <see cref="LatticeErrorEnum.Unavailable"/> and
    /// the instance is closed. A failure that another attempt cannot mend ends the open at once
    /// with <see cref="LatticeErrorEnum.Unavailable"/>, without waiting for the time: the master
    /// refuses the child, what the master sent is not valid, or the <see cref="MemoryLimit"/> of
    /// the child cannot hold the database. The message names the reason.
    /// </para>
    /// <para>
    /// <see cref="Validate"/> refuses zero, a negative value, <see cref="Timeout.InfiniteTimeSpan"/>
    /// and a value of more than one day, for a master as for a child.
    /// </para>
    /// </remarks>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long an operation may wait for this instance to reach a position: the position
    /// required by a <see cref="LatticeSession"/>, or, on a child, the position of a write
    /// it has just submitted. A session can set its own value. The wait is always finite.
    /// The default of 10 seconds is provisional.
    /// </summary>
    /// <remarks>
    /// When an operation takes a session that sets its own value, that value bounds the waits of
    /// the operation on this instance instead of this one: the wait for the position of the
    /// session and, on a child, the wait of a write until the child has applied it. No timeout
    /// travels with a write through a child: the master orders the write after the position of
    /// the session and bounds that wait with its own value.
    /// </remarks>
    public TimeSpan SynchronizationTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The temporary memory, in bytes, that one enumeration of this instance may use.
    /// <see langword="null"/>, the default: no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It counts the temporary memory the engine holds for one enumeration: the identities it
    /// has returned, with the text of their keys, the candidates it took from the index, and
    /// its workspace. That memory grows with the pass. It does not count the copies the
    /// enumeration returns, so an entity larger than the limit is read, nor the memory of the
    /// stored entities and their indexes. The limit reserves nothing: it takes no memory of its
    /// own, and several enumerations use it at the same time, each for itself.
    /// </para>
    /// <para>
    /// An enumeration that would exceed it fails with
    /// <see cref="LatticeErrorEnum.ReadLimitExceeded"/> and
    /// <see cref="LatticeLimitEnum.ReadMemory"/>, as <see cref="ILatticeSet{TEntity}.Enumerate"/>
    /// states. Without a limit an enumeration of any length runs to its end.
    /// </para>
    /// </remarks>
    public long? ReadMemoryLimit { get; init; }

    /// <summary>
    /// The time that one enumeration of this instance may spend inside the engine.
    /// <see langword="null"/>, the default: no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It counts the time inside the steps of the enumeration, added up over the pass, a wait
    /// inside the engine included. It does not count the wait for the position of a session,
    /// which <see cref="SynchronizationTimeout"/> or the timeout of the session bounds, nor the
    /// time between two steps: a caller that is slow between two entities does not use it up.
    /// The engine checks it at the points of a step where it can stop safely, so a step may
    /// end shortly after the limit is reached.
    /// </para>
    /// <para>
    /// An enumeration that would exceed it fails with
    /// <see cref="LatticeErrorEnum.ReadLimitExceeded"/> and
    /// <see cref="LatticeLimitEnum.ReadTime"/>, as <see cref="ILatticeSet{TEntity}.Enumerate"/>
    /// states. Without a limit an enumeration of any length runs to its end.
    /// </para>
    /// </remarks>
    public TimeSpan? ReadTimeLimit { get; init; }

    /// <summary>
    /// The memory budget of this instance, in bytes: the most memory the engine may hold for the
    /// database. <see langword="null"/>, the default: half of the memory the .NET runtime reports
    /// as available to the process, taken when the instance opens, and never less than 4 MiB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is half of <see cref="GCMemoryInfo.TotalAvailableMemoryBytes"/> of
    /// <see cref="GC.GetGCMemoryInfo()"/>, read when the instance opens, and never less than
    /// 4 MiB, the smallest value the option accepts; the default is never refused. On a machine
    /// without limits that figure is its physical memory. In a container it is by default three quarters
    /// of the memory limit of the container. A heap hard limit configured for the garbage
    /// collector lowers it, although that setting limits the managed heap and says nothing about
    /// the memory the engine needs. The budget in force is fixed when the instance opens and does
    /// not follow later changes of the memory available.
    /// </para>
    /// <para>
    /// The default is per instance: each instance computes it from the memory of the whole
    /// process, so two instances that rely on it may together take all of that memory. Set the
    /// value explicitly when one process opens several instances, in production, and whenever
    /// the heap of the garbage collector is limited. A value above the physical memory is
    /// allowed, for an application that prefers its memory to be paged out rather than have
    /// writes refused.
    /// </para>
    /// <para>
    /// The budget counts what the engine holds, not the managed heap of the application, nor what
    /// an enumeration remembers, which <see cref="ReadMemoryLimit"/> bounds when it is set. It
    /// counts the native memory in which the entities are stored, with the room that writes in
    /// progress have prepared there and the memory a write replaced or removed until the reads
    /// that could still reach it have left. The indexes, the text that Dictionary storage shares
    /// and the entries the engine keeps to find each entity are not counted yet. The budget is not
    /// a limit on the memory of the process.
    /// </para>
    /// <para>
    /// A write that the budget cannot hold is refused before it has any effect, with
    /// <see cref="LatticeErrorEnum.WriteRejected"/> and <see cref="LatticeLimitEnum.Memory"/>,
    /// and never runs later. Before it is refused it waits for the reads that still hold
    /// replaced memory and for the writes submitted before it, so it is refused only when the
    /// budget cannot hold it once they are done. A write that waits for its turn holds nothing of
    /// the budget: any number of writes may wait, and none is refused for their number.
    /// </para>
    /// <para>
    /// A value set smaller than 4 MiB (4,194,304 bytes), the unit in which the engine takes
    /// memory, is refused by <see cref="Validate"/>.
    /// </para>
    /// </remarks>
    public long? MemoryLimit { get; init; }

    // One segment of the arena of the engine, the unit in which it takes native memory, and so the
    // smallest MemoryLimit: an arena holds at least one segment. Validate refuses an explicit value
    // below it, and the computed default is never less than it. The engine takes the default size
    // of its segments from here (Storage.ArenaOptions.SegmentSize), so that Validate and the arena
    // agree on it.
    internal const int ArenaSegmentSize = 4 * 1024 * 1024;

    /// <summary>
    /// Checks that the options describe a supported instance.
    /// </summary>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.InvalidConfiguration"/>: a required value is missing, a
    /// timeout or a limit is out of its range, such as a limit that is set and not positive or a
    /// <see cref="MemoryLimit"/> that is set and smaller than 4 MiB, an address does not have the
    /// form that <see cref="ListenUri"/> states, or the values are incompatible.
    /// </exception>
    public void Validate()
    {
        if (!IsFiniteTimeout(SynchronizationTimeout))
        {
            throw Invalid($"{nameof(SynchronizationTimeout)} must be finite and positive.");
        }

        // Checked for every role: a child waits for its master by it, and a master gives a
        // connecting node the same time to introduce itself.
        if (!IsFiniteTimeout(OpenTimeout))
        {
            throw Invalid($"{nameof(OpenTimeout)} must be finite and positive.");
        }

        if (MemoryLimit is { } memory && memory < ArenaSegmentSize)
        {
            throw Invalid($"{nameof(MemoryLimit)} must be at least {ArenaSegmentSize} bytes, one segment of the memory of the engine, when it is set; leave it null for the default.");
        }

        if (ReadMemoryLimit is { } readMemory && readMemory <= 0)
        {
            throw Invalid($"{nameof(ReadMemoryLimit)} must be positive when it is set; leave it null for no limit.");
        }

        if (ReadTimeLimit is { } readTime && readTime <= TimeSpan.Zero)
        {
            throw Invalid($"{nameof(ReadTimeLimit)} must be positive when it is set; leave it null for no limit.");
        }

        switch (Role)
        {
            case LatticeRoleEnum.Master:
                ValidateMaster();
                break;
            case LatticeRoleEnum.Child:
                ValidateChild();
                break;
            default:
                throw Invalid($"Unknown role '{Role}'.");
        }
    }

    // A wait must end: zero, negative, infinite and absurdly long values are refused.
    internal static bool IsFiniteTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero && timeout <= TimeSpan.FromDays(1);

    private void ValidateMaster()
    {
        if (MasterUri is not null)
        {
            throw Invalid($"{nameof(MasterUri)} is not allowed for a master.");
        }

        if (ListenUri is { IsAbsoluteUri: false })
        {
            throw Invalid($"{nameof(ListenUri)} must be an absolute address.");
        }

        switch (PersistenceMode)
        {
            case LatticePersistenceModeEnum.Durable:
                if (string.IsNullOrWhiteSpace(StorageConnectionString))
                {
                    throw Invalid($"A Durable master requires {nameof(StorageConnectionString)}.");
                }

                break;
            case LatticePersistenceModeEnum.MemoryOnly:
                if (StorageConnectionString is not null)
                {
                    throw Invalid($"{nameof(StorageConnectionString)} is not allowed in MemoryOnly mode.");
                }

                if (Schema is null)
                {
                    throw Invalid($"A MemoryOnly master requires {nameof(Schema)}.");
                }

                break;
            default:
                throw Invalid($"Unknown persistence mode '{PersistenceMode}'.");
        }
    }

    private void ValidateChild()
    {
        if (PersistenceMode != LatticePersistenceModeEnum.MemoryOnly)
        {
            throw Invalid("A child must explicitly select MemoryOnly persistence.");
        }

        if (MasterUri is null || !MasterUri.IsAbsoluteUri)
        {
            throw Invalid($"A child requires an absolute {nameof(MasterUri)}.");
        }

        if (ListenUri is not null)
        {
            throw Invalid($"{nameof(ListenUri)} is not allowed for a child.");
        }

        if (StorageConnectionString is not null)
        {
            throw Invalid($"{nameof(StorageConnectionString)} is not allowed for a child.");
        }

        if (Schema is not null)
        {
            throw Invalid($"{nameof(Schema)} is not allowed for a child; the schema comes from the master.");
        }
    }

    private static LatticeException Invalid(string message) =>
        new(LatticeErrorEnum.InvalidConfiguration, message);
}
