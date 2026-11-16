using Moltaro.Lattice.Access;

namespace Moltaro.Lattice;

/// <summary>
/// Which limit refused an operation, as <see cref="LatticeException.Limit"/> names it.
/// Members are added as limits are; none is ever renumbered.
/// </summary>
public enum LatticeLimitEnum
{
    /// <summary>
    /// The temporary memory of a read: <see cref="LatticeReadLimits.Memory"/>, or
    /// <see cref="LatticeDatabaseOptions.ReadMemoryLimit"/> when the read set none.
    /// </summary>
    ReadMemory = 1,

    /// <summary>
    /// The time of a read inside the engine: <see cref="LatticeReadLimits.Time"/>, or
    /// <see cref="LatticeDatabaseOptions.ReadTimeLimit"/> when the read set none.
    /// </summary>
    ReadTime = 2,
}
