namespace Moltaro.Lattice.DependencyInjection.Tests;

internal sealed class User
{
    public long Id { get; set; }

    public string Name { get; set; } = "";
}

// An application service that gets the database from the container.
internal sealed class UserDirectory(ILatticeDatabase database)
{
    public async ValueTask AddAsync(long id, string name) => await database.Set<User>().InsertAsync(new User { Id = id, Name = name });

    public string? NameOf(long id) => database.Set<User>().Get(id)?.Name;
}

internal static class TestApp
{
    public static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "SchemaVersion": 1,
          "EntityTypes": [
            {
              "Id": 1,
              "Name": "User",
              "ClrType": "Moltaro.Lattice.DependencyInjection.Tests.User",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Name", "Type": "String" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    public static LatticeDatabaseOptions MasterOptions(Uri? listenUri = null) => new()
    {
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        Schema = Schema,
        ListenUri = listenUri,
    };

    public static LatticeDatabaseOptions ChildOptions(Uri masterUri) => new()
    {
        Role = LatticeRoleEnum.Child,
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        MasterUri = masterUri,
    };

    // An application with one Lattice database, the way an application sets it up.
    public static IHost BuildApp(LatticeDatabaseOptions options, Action<IServiceCollection>? configure = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Services.AddLattice(options);
        builder.Services.AddSingleton<UserDirectory>();
        configure?.Invoke(builder.Services);
        return builder.Build();
    }
}
