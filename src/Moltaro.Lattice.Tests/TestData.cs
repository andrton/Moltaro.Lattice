namespace Moltaro.Lattice.Tests;

// Bound to the entity type "User" by "ClrType" in the schema below.
internal sealed class User
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    public string? Email { get; set; }

    public int? Age { get; set; }

    public bool Enabled { get; set; }

    // Not declared in the schema, so it is not stored.
    public string? Note { get; set; }
}

// Bound to the entity type "Tag"; its primary key is a string.
internal sealed class Tag
{
    public string Name { get; set; } = "";

    public string Color { get; set; } = "";
}

internal static class TestData
{
    // User and Tag have .NET classes. Setting has none and is used only by name.
    // User has an index on one field and an index on two; Setting has an index too.
    public const string SchemaJson = """
        {
          "SchemaVersion": 3,
          "EntityTypes": [
            {
              "Id": 1,
              "Name": "User",
              "ClrType": "Moltaro.Lattice.Tests.User",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Name", "Type": "String" },
                { "Id": 3, "Name": "Email", "Type": "String", "Nullable": true },
                { "Id": 4, "Name": "Age", "Type": "Int32", "Nullable": true },
                { "Id": 5, "Name": "Enabled", "Type": "Boolean" }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByEmail", "Fields": [3] },
                { "Id": 2, "Name": "ByAgeAndName", "Fields": [4, 2] }
              ]
            },
            {
              "Id": 2,
              "Name": "Tag",
              "ClrType": "Moltaro.Lattice.Tests.Tag",
              "Fields": [
                { "Id": 1, "Name": "Name", "Type": "String" },
                { "Id": 2, "Name": "Color", "Type": "String" }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 3,
              "Name": "Setting",
              "Fields": [
                { "Id": 1, "Name": "Name", "Type": "String" },
                { "Id": 2, "Name": "Value", "Type": "String" },
                { "Id": 3, "Name": "Priority", "Type": "Int32" },
                { "Id": 4, "Name": "Payload", "Type": "Binary", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByPriority", "Fields": [3] }
              ]
            }
          ]
        }
        """;

    public static readonly LatticeSchema Schema = LatticeSchema.Compile(SchemaJson);

    public static ValueTask<ILatticeDatabase> OpenMasterAsync(Uri? listenUri = null, LatticeSchema? schema = null) =>
        LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema ?? Schema,
            ListenUri = listenUri,
        });

    public static ValueTask<ILatticeDatabase> OpenChildAsync(Uri masterUri, CancellationToken cancellationToken = default) =>
        LatticeDatabase.OpenAsync(
            new LatticeDatabaseOptions
            {
                Role = LatticeRoleEnum.Child,
                PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
                MasterUri = masterUri,
            },
            cancellationToken);

    public static ILatticeSet<User> Users(this ILatticeDatabase database) => database.Set<User>();

    public static async Task<LatticeException> ExpectErrorAsync(LatticeErrorEnum expected, Func<ValueTask<LatticeWriteResult>> write)
    {
        var exception = await Assert.ThrowsAsync<LatticeException>(async () => await write());
        Assert.Equal(expected, exception.Error);
        return exception;
    }

    public static void ExpectError(LatticeErrorEnum expected, Action action)
    {
        var exception = Assert.Throws<LatticeException>(action);
        Assert.Equal(expected, exception.Error);
    }
}
