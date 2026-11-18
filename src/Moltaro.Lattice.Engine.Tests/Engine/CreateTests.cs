using Moltaro.Lattice.Schema;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// EngineDatabase.Create, the internal entry to the engine until S12 (exception X4), makes an
// instance without opening it and refuses in the order of LatticeDatabase.Create (brief of Epic 1,
// sprint S06, "Create"): ArgumentNullException for null options; then Validate, which gives
// InvalidConfiguration; then NotSupportedException for Durable persistence (until S27). An instance is
// a MemoryOnly master, with or without a ListenUri, or a child (S10); how the engine refuses an address
// it cannot use is stated when the instance opens (ListenAddressTests).
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

    // Each of these options is invalid, and the invalid configuration is found first, before anything
    // the engine does not serve yet is refused, as those that ask for Durable persistence would be.
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

    [Fact]
    public void Durable_master_is_not_served_yet_and_is_NotSupportedException()
    {
        Assert.Throws<NotSupportedException>(() => EngineDatabase.Create(new LatticeDatabaseOptions { StorageConnectionString = "Host=localhost", Schema = Schema }));
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

    [Fact]
    public void Master_with_a_ListenUri_and_a_child_are_created_without_being_opened()
    {
        using var master = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = Schema,
            ListenUri = MasterAndChild.Loopback,
        });
        using var child = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = new Uri("lattice://127.0.0.1:7400"),
        });

        Assert.Equal(LatticeDatabaseStateEnum.Created, master.State);
        Assert.Equal(LatticeDatabaseStateEnum.Created, child.State);
    }

    private static LatticeDatabaseOptions Invalid(string options) => options switch
    {
        "a Durable master without a connection string" => new LatticeDatabaseOptions { Schema = Schema },
        "a Durable master with an infinite synchronization timeout" => new LatticeDatabaseOptions { StorageConnectionString = "Host=localhost", SynchronizationTimeout = Timeout.InfiniteTimeSpan },
        "a Durable child" => new LatticeDatabaseOptions { Role = LatticeRoleEnum.Child, MasterUri = new Uri("lattice://127.0.0.1:7400") },
        "a child with a schema" => new LatticeDatabaseOptions { Role = LatticeRoleEnum.Child, PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, MasterUri = new Uri("lattice://127.0.0.1:7400"), Schema = Schema },
        "a child without the address of its master" => new LatticeDatabaseOptions { Role = LatticeRoleEnum.Child, PersistenceMode = LatticePersistenceModeEnum.MemoryOnly },
        "a master with a relative ListenUri" => new LatticeDatabaseOptions { PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, Schema = Schema, ListenUri = new Uri("master", UriKind.Relative) },
        "a MemoryOnly master with a ListenUri and without a schema" => new LatticeDatabaseOptions { PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, ListenUri = new Uri("lattice://127.0.0.1:7400") },
        _ => throw new ArgumentOutOfRangeException(nameof(options), options, "No such options."),
    };
}
