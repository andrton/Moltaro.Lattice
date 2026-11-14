using Microsoft.Extensions.Hosting;

namespace Moltaro.Lattice.DependencyInjection;

// Opens one registered database when the application starts and closes it when the
// application stops. Every registered database has its own instance of this service.
internal sealed class LatticeBackgroundService : IHostedService
{
    private readonly ILatticeDatabase _database;

    public LatticeBackgroundService(ILatticeDatabase database)
    {
        _database = database;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _database.OpenAsync(cancellationToken).AsTask();

    public Task StopAsync(CancellationToken cancellationToken) => _database.CloseAsync(cancellationToken).AsTask();
}
