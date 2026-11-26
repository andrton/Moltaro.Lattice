namespace Moltaro.Lattice;

/// <summary>
/// Which limit refused an operation, as <see cref="LatticeException.Limit"/> names it.
/// Members are added as limits are; none is ever renumbered.
/// </summary>
public enum LatticeLimitEnum
{
    /// <summary>
    /// The temporary memory of a read: <see cref="LatticeDatabaseOptions.ReadMemoryLimit"/>.
    /// </summary>
    ReadMemory = 1,

    /// <summary>
    /// The time of a read inside the engine: <see cref="LatticeDatabaseOptions.ReadTimeLimit"/>.
    /// </summary>
    ReadTime = 2,

    /// <summary>
    /// The memory budget of the instance: <see cref="LatticeDatabaseOptions.MemoryLimit"/>. A
    /// write it cannot hold is refused with <see cref="LatticeErrorEnum.WriteRejected"/>.
    /// </summary>
    Memory = 3,

    /// <summary>
    /// The engine has no addressable root slot for a new entity lifetime. A write that needs
    /// another slot is refused with <see cref="LatticeErrorEnum.WriteRejected"/>.
    /// </summary>
    RootSlots = 4,
}
