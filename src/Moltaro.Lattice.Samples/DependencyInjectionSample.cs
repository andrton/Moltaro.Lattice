using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moltaro.Lattice.DependencyInjection;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples;

// The database in an application with dependency injection: one singleton, opened when the
// application starts, injected into services. A Web API is set up the same way:
//   builder.Services.AddLattice(...);
//   app.MapGet("/users/{id}", (long id, ILatticeDatabase database) => database.Set<User>().Get(id));
internal static class DependencyInjectionSample
{
    public static async Task RunAsync(LatticeSchema schema)
    {
        Console.WriteLine("== Dependency injection and background services ==");

        var builder = Host.CreateEmptyApplicationBuilder(settings: null);

        // One line registers the database. It is added before the services that use it,
        // so it is open when they start and still open while they stop.
        builder.Services.AddLattice(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
        });

        builder.Services.AddSingleton<UserReport>();
        builder.Services.AddHostedService<UserImportService>();

        using var app = builder.Build();

        // Start: the database opens, then the background service starts.
        await app.StartAsync();

        var import = app.Services.GetServices<IHostedService>().OfType<UserImportService>().Single();
        await import.Completed;

        // Any service gets the same database.
        app.Services.GetRequiredService<UserReport>().Print();

        // Stop: the background service stops, then the database closes.
        await app.StopAsync();
        Console.WriteLine($"Database after the application stopped: {app.Services.GetRequiredService<ILatticeDatabase>().State}");

        await RunSeveralDatabasesAsync(schema);
        Console.WriteLine();
    }

    // An application is not limited to one database: besides the default one it can have
    // any number of named databases, masters and replicas alike.
    private static async Task RunSeveralDatabasesAsync(LatticeSchema schema)
    {
        var options = new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
        };

        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Services.AddLattice("directory", options);
        builder.Services.AddLattice("archive", options);
        builder.Services.AddSingleton<UserArchiver>();

        using var app = builder.Build();
        await app.StartAsync();

        var directory = app.Services.GetRequiredKeyedService<ILatticeDatabase>("directory");
        await directory.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada", Enabled = true });

        await app.Services.GetRequiredService<UserArchiver>().ArchiveAsync(1);

        await app.StopAsync();
    }

    // A service that works with two databases; each is injected by its name.
    private sealed class UserArchiver(
        [FromKeyedServices("directory")] ILatticeDatabase directory,
        [FromKeyedServices("archive")] ILatticeDatabase archive)
    {
        public async Task ArchiveAsync(long id)
        {
            if (directory.Set<User>().Get(id) is not { } user)
            {
                return;
            }

            await archive.Set<User>().InsertAsync(user);
            await directory.Set<User>().DeleteAsync(id);
            Console.WriteLine($"Two databases: moved user {id} from 'directory' ({directory.Set<User>().Enumerate().Count()} left) to 'archive' ({archive.Set<User>().Enumerate().Count()} stored)");
        }
    }

    // A background service that writes.
    private sealed class UserImportService(ILatticeDatabase database) : BackgroundService
    {
        private readonly TaskCompletionSource _completed = new();

        public Task Completed => _completed.Task;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var users = database.Set<User>();
            for (var id = 1; id <= 3; id++)
            {
                await users.UpsertAsync(new User { Id = id, Name = $"Imported user {id}", Enabled = true }, stoppingToken);
            }

            _completed.SetResult();
        }
    }

    // An ordinary service that reads.
    private sealed class UserReport(ILatticeDatabase database)
    {
        public void Print()
        {
            foreach (var user in database.Set<User>().Enumerate().OrderBy(user => user.Id))
            {
                Console.WriteLine($"Report: {user}");
            }
        }
    }
}
