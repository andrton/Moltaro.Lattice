namespace Moltaro.Lattice.Tests;

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

    // The position of the same history with the next sequence: ahead of a database that is at the
    // given position, which only a caller can make up. It is made up from the text of the position,
    // whose form the documentation does not describe: its history, a hyphen and its sequence in
    // hexadecimal, as LatticePosition.ToString writes it.
    public static LatticePosition Ahead(LatticePosition position)
    {
        var text = position.ToString();
        var separator = text.IndexOf('-', StringComparison.Ordinal);
        var sequence = ulong.Parse(text.AsSpan(separator + 1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);

        return LatticePosition.Parse($"{text[..separator]}-{sequence + 1:x}");
    }

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

    // A write refused by the validation of an entity, with the field named.
    public static async Task ExpectValidationFailedAsync(string field, Func<ValueTask<LatticeWriteResult>> write)
    {
        var exception = await ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, write);
        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
    }

    // An operation that adding to a write batch refuses by the validation of an entity, with the
    // field named.
    public static void ExpectValidationFailed(string field, Action add)
    {
        var exception = Assert.Throws<LatticeException>(add);
        Assert.Equal(LatticeErrorEnum.ValidationFailed, exception.Error);
        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
    }
}
