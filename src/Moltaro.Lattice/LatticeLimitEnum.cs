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
}
