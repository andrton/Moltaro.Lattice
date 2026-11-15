using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// EngineDatabase.Create, the internal entry to the engine until S12 (exception X4), makes an
// instance without opening it and refuses in the order of LatticeDatabase.Create (brief of Epic 1,
// sprint S06, "Create"): ArgumentNullException for null options; then Validate, which gives
// InvalidConfiguration; then NotSupportedException for Durable persistence (until S27), for a
// child role (until S10) and for a ListenUri (until S10). In S06 an instance is a MemoryOnly master.
public sealed class CreateTests
{
    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        { "EntityTypes": [ { "Id": 1, "Name": "Note", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ], "PrimaryKey": [1] } ] }
        """);

    [Fact]
    public void Null_options_are_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => EngineDatabase.Create(null!));
    }

    // Each of these options is invalid, and each would also ask for what the engine does not serve
    // yet: the invalid configuration is found first.
    [Theory]
    [InlineData("a Durable master without a connection string")]
    [InlineData("a Durable master with an infinite synchronization timeout")]
    [InlineData("a Durable child")]
    [InlineData("a child with a schema")]
    [InlineData("a child without the address of its master")]
    [InlineData("a master with a relative ListenUri")]
    [InlineData("a MemoryOnly master with a ListenUri and without a schema")]
    public void Invalid_options_are_InvalidConfiguration_before_anything_is_refused_as_not_supported(string options)
    {
        var exception = Assert.Throws<LatticeException>(() => EngineDatabase.Create(Invalid(options)));

        Assert.Equal(LatticeErrorEnum.InvalidConfiguration, exception.Error);
    }

    [Theory]
    [InlineData("a Durable master")]
    [InlineData("a child")]
    [InlineData("a MemoryOnly master with a ListenUri")]
    public void Valid_options_that_the_engine_does_not_serve_yet_are_NotSupportedException(string options)
    {
        Assert.Throws<NotSupportedException>(() => EngineDatabase.Create(NotServed(options)));
    }

    [Fact]
    public void MemoryOnly_master_is_created_without_being_opened()
    {
        using var database = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = Schema,
        });

        Assert.Equal(LatticeDatabaseStateEnum.Created, database.State);
    }

    private static LatticeDatabaseOptions Invalid(string options) => options switch
    {
        "a Durable master without a connection string" => new LatticeDatabaseOptions { Schema = Schema },
        "a Durable master with an infinite synchronization timeout" => new LatticeDatabaseOptions { StorageConnectionString = "Host=localhost", SynchronizationTimeout = Timeout.InfiniteTimeSpan },
        "a Durable child" => new LatticeDatabaseOptions { Role = LatticeRoleEnum.Child, MasterUri = new Uri("lattice://master") },
        "a child with a schema" => new LatticeDatabaseOptions { Role = LatticeRoleEnum.Child, PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, MasterUri = new Uri("lattice://master"), Schema = Schema },
        "a child without the address of its master" => new LatticeDatabaseOptions { Role = LatticeRoleEnum.Child, PersistenceMode = LatticePersistenceModeEnum.MemoryOnly },
        "a master with a relative ListenUri" => new LatticeDatabaseOptions { PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, Schema = Schema, ListenUri = new Uri("master", UriKind.Relative) },
        "a MemoryOnly master with a ListenUri and without a schema" => new LatticeDatabaseOptions { PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, ListenUri = new Uri("lattice://master") },
        _ => throw new ArgumentOutOfRangeException(nameof(options), options, "No such options."),
    };

    private static LatticeDatabaseOptions NotServed(string options) => options switch
    {
        "a Durable master" => new LatticeDatabaseOptions { StorageConnectionString = "Host=localhost", Schema = Schema },
        "a child" => new LatticeDatabaseOptions { Role = LatticeRoleEnum.Child, PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, MasterUri = new Uri("lattice://master") },
        "a MemoryOnly master with a ListenUri" => new LatticeDatabaseOptions { PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, Schema = Schema, ListenUri = new Uri("lattice://master") },
        _ => throw new ArgumentOutOfRangeException(nameof(options), options, "No such options."),
    };
}
