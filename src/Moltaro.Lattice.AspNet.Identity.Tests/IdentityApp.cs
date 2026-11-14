namespace Moltaro.Lattice.AspNet.Identity.Tests;

// An application with ASP.NET Core Identity on a Lattice database, set up the way an
// application does it: a database with the application's schema, Identity, and the
// Lattice stores in place of the Entity Framework ones.
internal sealed class IdentityApp : IAsyncDisposable
{
    private readonly ILatticeDatabase _database;
    private readonly ServiceProvider _services;

    private IdentityApp(ILatticeDatabase database, ServiceProvider services)
    {
        _database = database;
        _services = services;
    }

    public static async Task<IdentityApp> StartAsync(Action<IdentityOptions>? configure = null)
    {
        var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = AppSchema.Compiled,
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(database);
        services
            .AddIdentityCore<AppUser>(options => configure?.Invoke(options))
            .AddRoles<AppRole>()
            .AddLatticeStores();

        return new IdentityApp(database, services.BuildServiceProvider());
    }

    // One unit of work of the application, such as one web request: the stores are scoped.
    public IdentityScope NewScope() => new(_services.CreateAsyncScope());

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
    }
}
