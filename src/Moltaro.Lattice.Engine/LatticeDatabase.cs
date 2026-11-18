using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice;

/// <summary>
/// Entry point: creates and opens databases.
/// </summary>
public static class LatticeDatabase
{
    /// <summary>
    /// Creates a database instance without opening it. Open it with
    /// <see cref="ILatticeDatabase.OpenAsync"/>.
    /// </summary>
    /// <remarks>
    /// Use this when the object must exist before the database is opened, for example to
    /// register it as a singleton and open it when the application starts. Otherwise
    /// <see cref="OpenAsync"/> does both steps.
    /// </remarks>
    /// <param name="options">The configuration of the database instance.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.InvalidConfiguration"/>: the options are not valid.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Durable persistence was selected; it is not implemented yet.
    /// </exception>
    public static ILatticeDatabase Create(LatticeDatabaseOptions options)
    {
        EngineDatabase.Check(options);

        return new EngineDatabase(options, testInputs: null);
    }

    /// <summary>
    /// Creates a database instance and opens it. Completes when the database is
    /// <see cref="LatticeDatabaseStateEnum.Ready"/>; see <see cref="ILatticeDatabase.OpenAsync"/>.
    /// </summary>
    /// <param name="options">The configuration of the database instance.</param>
    /// <param name="cancellationToken">Cancels opening.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.InvalidConfiguration"/>: the options are not valid.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Durable persistence was selected; it is not implemented yet.
    /// </exception>
    public static async ValueTask<ILatticeDatabase> OpenAsync(LatticeDatabaseOptions options, CancellationToken cancellationToken = default)
    {
        var database = Create(options);
        await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return database;
    }
}
