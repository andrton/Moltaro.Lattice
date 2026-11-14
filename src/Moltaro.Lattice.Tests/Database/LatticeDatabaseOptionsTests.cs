namespace Moltaro.Lattice.Tests.Database;

public sealed class LatticeDatabaseOptionsTests
{
    private static readonly LatticeSchema Schema = TestData.Schema;
    private const string Storage = "Host=localhost";
    private static readonly Uri Master = new("https://master.example");

    [Fact]
    public void Defaults_select_a_durable_master()
    {
        var options = new LatticeDatabaseOptions();

        Assert.Equal(LatticeRoleEnum.Master, options.Role);
        Assert.Equal(LatticePersistenceModeEnum.Durable, options.PersistenceMode);
    }

    [Fact]
    public void Durable_master_with_storage_is_valid()
    {
        new LatticeDatabaseOptions { StorageConnectionString = Storage }.Validate();
        new LatticeDatabaseOptions { StorageConnectionString = Storage, Schema = Schema }.Validate();
    }

    [Fact]
    public void Durable_master_requires_storage()
    {
        AssertInvalid(new LatticeDatabaseOptions { Schema = Schema });
    }

    [Fact]
    public void Memory_only_master_with_schema_is_valid()
    {
        new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = Schema,
        }.Validate();
    }

    [Fact]
    public void Memory_only_master_requires_schema()
    {
        AssertInvalid(new LatticeDatabaseOptions { PersistenceMode = LatticePersistenceModeEnum.MemoryOnly });
    }

    [Fact]
    public void Memory_only_master_rejects_storage()
    {
        AssertInvalid(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = Schema,
            StorageConnectionString = Storage,
        });
    }

    [Fact]
    public void Master_rejects_master_uri()
    {
        AssertInvalid(new LatticeDatabaseOptions { StorageConnectionString = Storage, MasterUri = Master });
    }

    [Fact]
    public void Memory_only_child_with_master_uri_is_valid()
    {
        Child(Master).Validate();
    }

    [Fact]
    public void Child_is_never_silently_downgraded_from_durable()
    {
        AssertInvalid(new LatticeDatabaseOptions { Role = LatticeRoleEnum.Child, MasterUri = Master });
    }

    [Fact]
    public void Child_requires_an_absolute_master_uri()
    {
        AssertInvalid(Child(masterUri: null));
        AssertInvalid(Child(new Uri("master", UriKind.Relative)));
    }

    [Fact]
    public void Child_rejects_storage_and_schema()
    {
        AssertInvalid(Child(Master, storage: Storage));
        AssertInvalid(Child(Master, schema: Schema));
    }

    private static LatticeDatabaseOptions Child(Uri? masterUri, string? storage = null, LatticeSchema? schema = null) =>
        new()
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = masterUri,
            StorageConnectionString = storage,
            Schema = schema,
        };

    private static void AssertInvalid(LatticeDatabaseOptions options)
    {
        var exception = Assert.Throws<LatticeException>(options.Validate);
        Assert.Equal(LatticeErrorEnum.InvalidConfiguration, exception.Error);
    }
}
