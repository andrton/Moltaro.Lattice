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
    /// Address at which a master accepts its children. Optional: a master without it serves
    /// only its own process. Not allowed for a child.
    /// </summary>
    public Uri? ListenUri { get; init; }

    /// <summary>
    /// Address of the master, equal to the master's <see cref="ListenUri"/>. Required for a
    /// child and not allowed for a master.
    /// </summary>
    public Uri? MasterUri { get; init; }

    /// <summary>
    /// How long an operation may wait for this instance to reach a position: the position
    /// required by a <see cref="LatticeSession"/>, or, on a child, the position of a write
    /// it has just submitted. A session can set its own value. The wait is always finite.
    /// The default of 10 seconds is provisional.
    /// </summary>
    public TimeSpan SynchronizationTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The temporary memory, in bytes, that one enumeration of this instance may use, unless
    /// the enumeration sets its own limit. <see langword="null"/>, the default: no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is counted, and how an enumeration that would exceed the limit fails, is stated at
    /// <see cref="LatticeReadLimits.Memory"/> and <see cref="LatticeReadLimits"/>; the error
    /// names <see cref="LatticeLimitEnum.ReadMemory"/>. An enumeration can set a larger or a
    /// smaller limit of its own, and none can lift this one. Without a limit an enumeration of
    /// any length runs to its end.
    /// </para>
    /// <para>
    /// <b>Not implemented yet.</b> The value is validated and otherwise not used: no
    /// enumeration is limited by it.
    /// </para>
    /// </remarks>
    public long? ReadMemoryLimit { get; init; }

    /// <summary>
    /// The time that one enumeration of this instance may spend inside the engine, unless the
    /// enumeration sets its own limit. <see langword="null"/>, the default: no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is counted, and how an enumeration that would exceed the limit fails, is stated at
    /// <see cref="LatticeReadLimits.Time"/> and <see cref="LatticeReadLimits"/>; the error
    /// names <see cref="LatticeLimitEnum.ReadTime"/>. The wait for the position of a session
    /// is not counted; <see cref="SynchronizationTimeout"/> bounds it. An enumeration can set
    /// a longer or a shorter limit of its own, and none can lift this one. Without a limit an
    /// enumeration of any length runs to its end.
    /// </para>
    /// <para>
    /// <b>Not implemented yet.</b> The value is validated and otherwise not used: no
    /// enumeration is limited by it.
    /// </para>
    /// </remarks>
    public TimeSpan? ReadTimeLimit { get; init; }

    /// <summary>
    /// Checks that the options describe a supported instance.
    /// </summary>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.InvalidConfiguration"/>: a required value is missing, a
    /// timeout or a limit is out of its range, such as a limit that is set and not positive,
    /// or the values are incompatible.
    /// </exception>
    public void Validate()
    {
        if (!IsFiniteTimeout(SynchronizationTimeout))
        {
            throw Invalid($"{nameof(SynchronizationTimeout)} must be finite and positive.");
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
