namespace Moltaro.Lattice.Tests;

// A field of an enum type holds the underlying integer: a record holds that integer, and a
// property of a bound class is either that integer or a .NET enum with the same underlying
// type. The .NET enum agrees with the enum type in its members and its Flags mark as well, see
// DotNetEnumAgreementTests: one that has two names for one value binds only when the schema
// declares both names, one of them canonical.
public sealed class EnumBindingTests
{
    private enum JobStateEnum : byte
    {
        Queued = 0,
        Running = 1,
        Done = 2,
    }

    // The values of JobStateEnum with other underlying types.
    private enum WideJobStateEnum
    {
        Queued = 0,
        Running = 1,
        Done = 2,
    }

    private enum SignedJobStateEnum : sbyte
    {
        Queued = 0,
        Running = 1,
        Done = 2,
    }

    private enum AccountStateEnum
    {
        Disabled = 0,
        Active = 1,
        Enabled = Active,
    }

    // Two names for 2, a value the schema does not declare.
    private enum PausableAccountStateEnum
    {
        Disabled = 0,
        Active = 1,
        Enabled = Active,
        Paused = 2,
        Halted = Paused,
    }

    [Fact]
    public async Task Property_of_a_dotnet_enum_binds_to_a_field_of_an_enum_type()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaFor<Job, Account>());

        var jobs = database.Set<Job>();

        Assert.Equal("Job", jobs.EntityType.Name);
        Assert.True(jobs.EntityType.TryGetField("State", out var state));
        Assert.Equal(typeof(byte), state.Declaration.ValueType);
    }

    [Fact]
    public async Task Property_of_the_underlying_integer_binds_to_a_field_of_an_enum_type()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaFor<JobWithIntegers, Account>());

        Assert.Equal("Job", database.Set<JobWithIntegers>().EntityType.Name);
    }

    [Fact]
    public async Task Dotnet_enum_with_another_underlying_type_does_not_bind()
    {
        await using var wide = await TestData.OpenMasterAsync(schema: SchemaFor<JobWithWideEnum, Account>());
        await using var signed = await TestData.OpenMasterAsync(schema: SchemaFor<JobWithSignedEnum, Account>());

        // The value would be read as another physical type: an Int32 for a UInt8, a signed
        // byte for an unsigned one.
        TestData.ExpectError(LatticeErrorEnum.BindingFailed, () => wide.Set<JobWithWideEnum>());
        TestData.ExpectError(LatticeErrorEnum.BindingFailed, () => signed.Set<JobWithSignedEnum>());
    }

    [Fact]
    public async Task Dotnet_enum_with_two_names_for_one_value_binds_when_the_schema_declares_that_value()
    {
        // The schema declares 1 as Active, its canonical name, and Enabled.
        await using var database = await TestData.OpenMasterAsync(schema: SchemaFor<Job, Account>());

        Assert.Equal("Account", database.Set<Account>().EntityType.Name);
    }

    [Fact]
    public async Task Dotnet_enum_with_two_names_for_a_value_the_schema_does_not_declare_does_not_bind()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaFor<Job, PausableAccount>());

        // Paused and Halted name 2, and the schema has no canonical name for 2.
        TestData.ExpectError(LatticeErrorEnum.BindingFailed, () => database.Set<PausableAccount>());
    }

    [Fact]
    public void Record_holds_the_underlying_integer_of_a_field_of_an_enum_type()
    {
        var schema = SchemaFor<Job, Account>();
        Assert.True(schema.TryGetEntityType("Job", out var job));

        var record = new LatticeRecord(job) { ["Id"] = 1L, ["State"] = (byte)2 };
        Assert.Equal((byte)2, record["State"]);

        // An integer of another width is accepted when it fits, as for any integer field.
        record["State"] = 1;
        Assert.Equal((byte)1, record["State"]);
        Assert.Throws<ArgumentException>(() => record["State"] = 256);
    }

    private static LatticeSchema SchemaFor<TJob, TAccount>() => LatticeSchema.Compile($$"""
        {
          "Enums": [
            {
              "Id": 1, "Name": "JobState", "UnderlyingType": "UInt8",
              "Members": [
                { "Name": "Queued", "Value": 0 },
                { "Name": "Running", "Value": 1 },
                { "Name": "Done", "Value": 2 }
              ]
            },
            {
              "Id": 2, "Name": "AccountState",
              "Members": [
                { "Name": "Disabled", "Value": 0 },
                { "Name": "Active", "Value": 1, "Canonical": true },
                { "Name": "Enabled", "Value": 1 }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Job", "ClrType": "{{typeof(TJob).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "State", "Type": "Enum", "Enum": "JobState" },
                { "Id": 3, "Name": "Previous", "Type": "Enum", "Enum": "JobState", "Nullable": true }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Account", "ClrType": "{{typeof(TAccount).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "State", "Type": "Enum", "Enum": "AccountState" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private sealed class Job
    {
        public long Id { get; set; }

        public JobStateEnum State { get; set; }

        public JobStateEnum? Previous { get; set; }
    }

    private sealed class JobWithIntegers
    {
        public long Id { get; set; }

        public byte State { get; set; }

        public byte? Previous { get; set; }
    }

    private sealed class JobWithWideEnum
    {
        public long Id { get; set; }

        public WideJobStateEnum State { get; set; }

        public WideJobStateEnum? Previous { get; set; }
    }

    private sealed class JobWithSignedEnum
    {
        public long Id { get; set; }

        public SignedJobStateEnum State { get; set; }

        public SignedJobStateEnum? Previous { get; set; }
    }

    private sealed class Account
    {
        public long Id { get; set; }

        public AccountStateEnum State { get; set; }
    }

    private sealed class PausableAccount
    {
        public long Id { get; set; }

        public PausableAccountStateEnum State { get; set; }
    }
}
