namespace Moltaro.Lattice;

/// <summary>
/// Persistence mode of a database instance. It is selected when the instance is created
/// and stays fixed for the instance lifetime.
/// </summary>
public enum LatticePersistenceModeEnum
{
    /// <summary>
    /// A write succeeds only after durable storage has acknowledged it and the change is
    /// published locally. The state is recovered when the database is opened again.
    /// </summary>
    Durable = 0,

    /// <summary>
    /// The engine persists nothing: no data, schema, journal or shutdown dump. A write
    /// succeeds after atomic local publication and does not survive the loss of the instance.
    /// </summary>
    MemoryOnly = 1,
}
