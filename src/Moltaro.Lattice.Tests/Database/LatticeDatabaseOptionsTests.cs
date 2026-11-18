namespace Moltaro.Lattice.Tests.Database;

public sealed class LatticeDatabaseOptionsTests
{
    private static readonly LatticeSchema Schema = TestData.Schema;
    private const string Storage = "Host=localhost";
    private const long FourMebibytes = 4 * 1024 * 1024;
    private static readonly Uri Master = new("lattice://127.0.0.1:7400");

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

    // The open timeout (brief of Epic 1, sprint S10): 30 seconds by default, finite and positive, and
    // checked by Validate for every role: a child waits for its master by it, and a master gives a
    // connecting node the same time to introduce itself.
    [Fact]
    public void Open_timeout_is_30_seconds_by_default_and_any_finite_positive_value_is_valid()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), new LatticeDatabaseOptions().OpenTimeout);

        Child(Master, openTimeout: TimeSpan.FromTicks(1)).Validate();
        Child(Master, openTimeout: TimeSpan.FromDays(1)).Validate();
    }

    [Fact]
    public void Open_timeout_that_is_zero_negative_infinite_or_longer_than_a_day_is_refused()
    {
        AssertInvalid(Child(Master, openTimeout: TimeSpan.Zero));
        AssertInvalid(Child(Master, openTimeout: TimeSpan.FromTicks(-1)));
        AssertInvalid(Child(Master, openTimeout: Timeout.InfiniteTimeSpan));
        AssertInvalid(Child(Master, openTimeout: TimeSpan.FromDays(1) + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void Open_timeout_out_of_its_range_is_refused_on_a_master_too()
    {
        AssertInvalid(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = Schema,
            OpenTimeout = TimeSpan.Zero,
        });
    }

    // The form of an address (brief of Epic 1, sprint S12; the remarks of ListenUri and MasterUri): the
    // scheme lattice, a host and a port, and nothing else. Validate refuses every other form of a
    // ListenUri and of a MasterUri with InvalidConfiguration, and port 0 in a MasterUri, where the port is
    // the one the master listens at.
    public static IEnumerable<TheoryDataRow<string>> OtherForms =>
    [
        new("another scheme"),
        new("no host"),
        new("no port"),
        new("a path"),
        new("a query"),
        new("a fragment"),
        new("user info"),
    ];

    [Fact]
    public void Address_with_the_scheme_lattice_a_host_and_a_port_is_valid()
    {
        MemoryOnlyMaster(listenUri: new Uri("lattice://127.0.0.1:7400")).Validate();
        MemoryOnlyMaster(listenUri: new Uri("lattice://localhost:7400")).Validate();
        MemoryOnlyMaster(listenUri: new Uri("lattice://127.0.0.1:0")).Validate();
        Child(new Uri("lattice://127.0.0.1:7400")).Validate();
        Child(new Uri("lattice://localhost:7400")).Validate();
    }

    [Theory(Skip = SkipReason.BeforeSprint12)]
    [MemberData(nameof(OtherForms))]
    public void ListenUri_of_another_form_is_refused(string form)
    {
        AssertInvalid(MemoryOnlyMaster(listenUri: AddressOf(form)));
    }

    [Theory(Skip = SkipReason.BeforeSprint12)]
    [MemberData(nameof(OtherForms))]
    public void MasterUri_of_another_form_is_refused(string form)
    {
        AssertInvalid(Child(AddressOf(form)));
    }

    [Fact(Skip = SkipReason.BeforeSprint12)]
    public void MasterUri_with_port_0_is_refused()
    {
        AssertInvalid(Child(new Uri("lattice://127.0.0.1:0")));
    }

    private static Uri AddressOf(string form) => form switch
    {
        "another scheme" => new Uri("tcp://127.0.0.1:7400"),
        "no host" => new Uri("lattice:///"),
        "no port" => new Uri("lattice://127.0.0.1"),
        "a path" => new Uri("lattice://127.0.0.1:7400/database"),
        "a query" => new Uri("lattice://127.0.0.1:7400?database=users"),
        "a fragment" => new Uri("lattice://127.0.0.1:7400#users"),
        "user info" => new Uri("lattice://admin@127.0.0.1:7400"),
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "No such form of an address."),
    };

    private static LatticeDatabaseOptions MemoryOnlyMaster(Uri listenUri) =>
        new()
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = Schema,
            ListenUri = listenUri,
        };

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

    private static LatticeDatabaseOptions Child(Uri masterUri, TimeSpan openTimeout) =>
        new()
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = masterUri,
            OpenTimeout = openTimeout,
        };

    private static void AssertInvalid(LatticeDatabaseOptions options)
    {
        var exception = Assert.Throws<LatticeException>(options.Validate);
        Assert.Equal(LatticeErrorEnum.InvalidConfiguration, exception.Error);
    }
}
