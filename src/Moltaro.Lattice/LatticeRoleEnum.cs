namespace Moltaro.Lattice;

/// <summary>
/// Role of a database instance in the replication topology.
/// </summary>
public enum LatticeRoleEnum
{
    /// <summary>
    /// The single authoritative instance. It validates, orders and executes every write.
    /// </summary>
    Master = 0,

    /// <summary>
    /// A read replica. It loads the whole database from its master, then applies the
    /// master's confirmed changes and serves reads from local memory.
    /// </summary>
    Child = 1,
}
