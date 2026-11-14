namespace Moltaro.Lattice.DependencyInjection.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public async Task Database_opens_when_the_application_starts_and_closes_when_it_stops()
    {
        using var app = TestApp.BuildApp(TestApp.MasterOptions());
        var database = app.Services.GetRequiredService<ILatticeDatabase>();
        Assert.Equal(LatticeDatabaseStateEnum.Created, database.State);

        await app.StartAsync();
        Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);

        await app.StopAsync();
        Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
    }

    [Fact]
    public async Task Database_is_one_singleton_for_the_whole_application()
    {
        using var app = TestApp.BuildApp(TestApp.MasterOptions());
        await app.StartAsync();

        using var scope = app.Services.CreateScope();

        Assert.Same(
            app.Services.GetRequiredService<ILatticeDatabase>(),
            scope.ServiceProvider.GetRequiredService<ILatticeDatabase>());

        await app.StopAsync();
    }

    [Fact]
    public async Task Services_get_the_database_injected()
    {
        using var app = TestApp.BuildApp(TestApp.MasterOptions());
        await app.StartAsync();

        // UserDirectory takes ILatticeDatabase in its constructor.
        var directory = app.Services.GetRequiredService<UserDirectory>();
        await directory.AddAsync(1, "Ada");

        Assert.Equal("Ada", directory.NameOf(1));
        Assert.Equal("Ada", app.Services.GetRequiredService<ILatticeDatabase>().Set("User").Get(1)?["Name"]);

        await app.StopAsync();
    }

    [Fact]
    public async Task Services_can_be_created_before_the_application_starts_but_not_used()
    {
        using var app = TestApp.BuildApp(TestApp.MasterOptions());

        // Construction does not touch the database.
        var directory = app.Services.GetRequiredService<UserDirectory>();
        var exception = Assert.Throws<LatticeException>(() => directory.NameOf(1));
        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);

        await app.StartAsync();
        Assert.Null(directory.NameOf(1));

        await app.StopAsync();
        Assert.Throws<ObjectDisposedException>(() => directory.NameOf(1));
    }

    [Fact]
    public async Task Background_service_registered_after_the_database_finds_it_open()
    {
        using var app = TestApp.BuildApp(TestApp.MasterOptions(), services => services.AddHostedService<Seeder>());

        await app.StartAsync();

        // The seeder wrote its user in StartAsync, which ran after the database had opened.
        Assert.Equal("seeded at startup", app.Services.GetRequiredService<UserDirectory>().NameOf(1));

        await app.StopAsync();
    }

    [Fact]
    public async Task Options_can_be_built_from_other_services()
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Services.AddSingleton(TestApp.Schema);
        builder.Services.AddLattice(services => new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = services.GetRequiredService<LatticeSchema>(),
        });
        using var app = builder.Build();

        await app.StartAsync();

        Assert.Same(TestApp.Schema, app.Services.GetRequiredService<ILatticeDatabase>().Schema);

        await app.StopAsync();
    }

    [Fact]
    public async Task Master_application_and_child_application_work_in_one_process()
    {
        var address = TestApp.NewAddress();
        using var masterApp = TestApp.BuildApp(TestApp.MasterOptions(address));
        using var childApp = TestApp.BuildApp(TestApp.ChildOptions(address));
        await masterApp.StartAsync();
        await childApp.StartAsync();

        // The same service class in both applications; it does not know the role.
        var masterDirectory = masterApp.Services.GetRequiredService<UserDirectory>();
        var childDirectory = childApp.Services.GetRequiredService<UserDirectory>();

        await masterDirectory.AddAsync(1, "written in the master application");
        await childDirectory.AddAsync(2, "written in the child application");

        Assert.Equal("written in the child application", masterDirectory.NameOf(2));
        await TestApp.EventuallyAsync(() => childDirectory.NameOf(1) is not null && childDirectory.NameOf(2) is not null);

        await childApp.StopAsync();
        await masterApp.StopAsync();
    }

    [Fact]
    public async Task Container_without_the_host_lets_the_application_open_the_database_itself()
    {
        var services = new ServiceCollection();
        services.AddLattice(TestApp.MasterOptions());
        services.AddSingleton<UserDirectory>();
        var provider = services.BuildServiceProvider();

        var database = provider.GetRequiredService<ILatticeDatabase>();
        await database.OpenAsync();
        await provider.GetRequiredService<UserDirectory>().AddAsync(1, "Ada");
        Assert.Equal("Ada", provider.GetRequiredService<UserDirectory>().NameOf(1));

        // Disposing the container closes the database.
        await provider.DisposeAsync();
        Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
    }

    [Fact]
    public async Task Application_does_not_start_with_invalid_options()
    {
        // A Durable master without storage.
        using var app = TestApp.BuildApp(new LatticeDatabaseOptions());

        var exception = await Assert.ThrowsAsync<LatticeException>(() => app.StartAsync());

        Assert.Equal(LatticeErrorEnum.InvalidConfiguration, exception.Error);
    }

    [Fact]
    public async Task Application_can_have_several_independent_databases()
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Services.AddLattice(TestApp.MasterOptions());
        builder.Services.AddLattice("archive", TestApp.MasterOptions());
        builder.Services.AddLattice("audit", TestApp.MasterOptions());
        using var app = builder.Build();
        await app.StartAsync();

        var main = app.Services.GetRequiredService<ILatticeDatabase>();
        var archive = app.Services.GetRequiredKeyedService<ILatticeDatabase>("archive");
        var audit = app.Services.GetRequiredKeyedService<ILatticeDatabase>("audit");

        // All are open, and what is written to one is not in the others.
        await main.Set<User>().InsertAsync(new User { Id = 1, Name = "main" });
        await archive.Set<User>().InsertAsync(new User { Id = 1, Name = "archive" });

        Assert.Equal("main", main.Set<User>().Get(1)?.Name);
        Assert.Equal("archive", archive.Set<User>().Get(1)?.Name);
        Assert.Null(audit.Set<User>().Get(1));

        await app.StopAsync();
        Assert.All([main, archive, audit], database => Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State));
    }

    [Fact]
    public async Task Application_can_have_several_replicas_of_different_masters()
    {
        var usersAddress = TestApp.NewAddress();
        var settingsAddress = TestApp.NewAddress();
        using var masters = BuildApp(services =>
        {
            services.AddLattice("users", TestApp.MasterOptions(usersAddress));
            services.AddLattice("settings", TestApp.MasterOptions(settingsAddress));
        });
        using var client = BuildApp(services =>
        {
            services.AddLattice("users", TestApp.ChildOptions(usersAddress));
            services.AddLattice("settings", TestApp.ChildOptions(settingsAddress));
            services.AddSingleton<TwoReplicas>();
        });
        await masters.StartAsync();
        await client.StartAsync();

        await masters.Services.GetRequiredKeyedService<ILatticeDatabase>("users").Set<User>().InsertAsync(new User { Id = 1, Name = "from users" });
        await masters.Services.GetRequiredKeyedService<ILatticeDatabase>("settings").Set<User>().InsertAsync(new User { Id = 1, Name = "from settings" });

        // A service gets each replica by name through its constructor.
        var replicas = client.Services.GetRequiredService<TwoReplicas>();
        await TestApp.EventuallyAsync(() => replicas.Users.Set<User>().Get(1) is not null && replicas.Settings.Set<User>().Get(1) is not null);
        Assert.Equal("from users", replicas.Users.Set<User>().Get(1)?.Name);
        Assert.Equal("from settings", replicas.Settings.Set<User>().Get(1)?.Name);

        await client.StopAsync();
        await masters.StopAsync();

        static IHost BuildApp(Action<IServiceCollection> configure)
        {
            var builder = Host.CreateEmptyApplicationBuilder(settings: null);
            configure(builder.Services);
            return builder.Build();
        }
    }

    [Fact]
    public void Each_database_is_registered_once()
    {
        var services = new ServiceCollection();
        services.AddLattice(TestApp.MasterOptions());
        services.AddLattice("archive", TestApp.MasterOptions());

        Assert.Throws<InvalidOperationException>(() => services.AddLattice(TestApp.MasterOptions()));
        Assert.Throws<InvalidOperationException>(() => services.AddLattice("archive", TestApp.MasterOptions()));
    }

    private sealed class TwoReplicas(
        [FromKeyedServices("users")] ILatticeDatabase users,
        [FromKeyedServices("settings")] ILatticeDatabase settings)
    {
        public ILatticeDatabase Users => users;

        public ILatticeDatabase Settings => settings;
    }

    private sealed class Seeder(ILatticeDatabase database) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            database.Set<User>().InsertAsync(new User { Id = 1, Name = "seeded at startup" }, cancellationToken).AsTask();

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
