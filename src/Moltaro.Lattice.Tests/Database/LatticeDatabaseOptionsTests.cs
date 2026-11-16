namespace Moltaro.Lattice.Tests.Database;

public sealed class LatticeDatabaseOptionsTests
{
    private static readonly LatticeSchema Schema = TestData.Schema;
    private const string Storage = "Host=localhost";
    private const long FourMebibytes = 4 * 1024 * 1024;
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

    [Fact]
    public void Read_limits_are_unset_by_default_and_any_positive_one_is_valid()
    {
        var options = new LatticeDatabaseOptions();

        Assert.Null(options.ReadMemoryLimit);
        Assert.Null(options.ReadTimeLimit);
        MemoryOnlyMaster(readMemoryLimit: 1, readTimeLimit: TimeSpan.FromTicks(1)).Validate();
        MemoryOnlyMaster(readMemoryLimit: long.MaxValue, readTimeLimit: TimeSpan.MaxValue).Validate();
    }

    [Fact]
    public void Read_limit_that_is_set_and_not_positive_is_refused()
    {
        AssertInvalid(MemoryOnlyMaster(readMemoryLimit: 0, readTimeLimit: null));
        AssertInvalid(MemoryOnlyMaster(readMemoryLimit: -1, readTimeLimit: null));
        AssertInvalid(MemoryOnlyMaster(readMemoryLimit: null, readTimeLimit: TimeSpan.Zero));
        AssertInvalid(MemoryOnlyMaster(readMemoryLimit: null, readTimeLimit: TimeSpan.FromTicks(-1)));
    }

    // The memory budget of an instance (brief of Epic 1, sprint S09): unset by default, and refused by
    // Validate when it is set below one segment of the memory of the engine, 4 MiB.
    [Fact]
    public void Memory_limit_is_unset_by_default_and_any_value_of_at_least_4_MiB_is_valid()
    {
        Assert.Null(new LatticeDatabaseOptions().MemoryLimit);

        MemoryOnlyMaster(memoryLimit: null).Validate();
        MemoryOnlyMaster(memoryLimit: FourMebibytes).Validate();
        MemoryOnlyMaster(memoryLimit: FourMebibytes + 1).Validate();
        MemoryOnlyMaster(memoryLimit: long.MaxValue).Validate();
    }

    [Fact]
    public void Memory_limit_that_is_set_and_smaller_than_4_MiB_is_refused()
    {
        AssertInvalid(MemoryOnlyMaster(memoryLimit: FourMebibytes - 1));
        AssertInvalid(MemoryOnlyMaster(memoryLimit: 0));
        AssertInvalid(MemoryOnlyMaster(memoryLimit: -1));
        AssertInvalid(MemoryOnlyMaster(memoryLimit: long.MinValue));
    }

    private static LatticeDatabaseOptions MemoryOnlyMaster(long? memoryLimit) =>
        new()
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = Schema,
            MemoryLimit = memoryLimit,
        };

    private static LatticeDatabaseOptions MemoryOnlyMaster(long? readMemoryLimit, TimeSpan? readTimeLimit) =>
        new()
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = Schema,
            ReadMemoryLimit = readMemoryLimit,
            ReadTimeLimit = readTimeLimit,
        };

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
