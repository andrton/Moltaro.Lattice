namespace Moltaro.Lattice.Tests.Indexes.Unique;

// The schemas of the tests of unique indexes beside UniqueIndexTests, each with one entity type:
// - Accounts: Account, bound to the class Account, as UniqueIndexTests declares it: Id, an Int64; Login, a
//   String in the unique index ByLogin; Tenant, a String, and Email, a nullable String, in the unique index
//   ByTenantAndEmail; ByTenant (Tenant), not unique.
// - Slots: Slot, by name: Id, an Int64; A, a nullable String; B, a nullable Int32; C, a nullable String;
//   ByABC (A, B, C), unique, every part of which can be null.
// - Valued: Valued, by name: Id, an Int64, and one nullable field of each kind of part, each alone in a
//   unique index named "By" and its name: Exact, a String compared Ordinal; Folded, a String compared
//   without case; Real, a Float64; Single, a Float32; Level, an Enum over Int16 whose members are -2 and 5.
// - Profiles: Profile, by name: Id, an Int64; Login, a String compared Ordinal; Nick, a String compared
//   without case; Note, a nullable String in no index. ByNick, Id 2, is declared before ByLogin, Id 1, so
//   that the order of the declaration is not the order of the identities; both are unique.
internal static class UniqueSchemas
{
    public static LatticeSchema Accounts { get; } = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Account", "ClrType": "{{typeof(Account).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Login", "Type": "String" },
                { "Id": 3, "Name": "Tenant", "Type": "String" },
                { "Id": 4, "Name": "Email", "Type": "String", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByLogin", "Fields": [2], "Unique": true },
                { "Id": 2, "Name": "ByTenantAndEmail", "Fields": [3, 4], "Unique": true },
                { "Id": 3, "Name": "ByTenant", "Fields": [3] }
              ]
            }
          ]
        }
        """);

    public static LatticeSchema Slots { get; } = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Slot",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "A", "Type": "String", "Nullable": true },
                { "Id": 3, "Name": "B", "Type": "Int32", "Nullable": true },
                { "Id": 4, "Name": "C", "Type": "String", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByABC", "Fields": [2, 3, 4], "Unique": true } ]
            }
          ]
        }
        """);

    public static LatticeSchema Profiles { get; } = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Profile",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Login", "Type": "String" },
                { "Id": 3, "Name": "Nick", "Type": "String", "Comparison": "OrdinalIgnoreCase" },
                { "Id": 4, "Name": "Note", "Type": "String", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 2, "Name": "ByNick", "Fields": [3], "Unique": true },
                { "Id": 1, "Name": "ByLogin", "Fields": [2], "Unique": true }
              ]
            }
          ]
        }
        """);

    // Valued in the encoding given, UTF-8 or UTF-16.
    public static LatticeSchema Valued(string encoding) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "Enums": [
            { "Id": 1, "Name": "Level", "UnderlyingType": "Int16", "Members": [ { "Name": "Low", "Value": -2 }, { "Name": "High", "Value": 5 } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Valued",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Exact", "Type": "String", "Nullable": true },
                { "Id": 3, "Name": "Folded", "Type": "String", "Nullable": true, "Comparison": "OrdinalIgnoreCase" },
                { "Id": 4, "Name": "Real", "Type": "Float64", "Nullable": true },
                { "Id": 5, "Name": "Single", "Type": "Float32", "Nullable": true },
                { "Id": 6, "Name": "Level", "Type": "Enum", "Enum": "Level", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByExact", "Fields": [2], "Unique": true },
                { "Id": 2, "Name": "ByFolded", "Fields": [3], "Unique": true },
                { "Id": 3, "Name": "ByReal", "Fields": [4], "Unique": true },
                { "Id": 4, "Name": "BySingle", "Fields": [5], "Unique": true },
                { "Id": 5, "Name": "ByLevel", "Fields": [6], "Unique": true }
              ]
            }
          ]
        }
        """);

    // A record of the entity type of the set with its Id and the values given by field; every other
    // field is null.
    public static LatticeRecord RecordOf(ILatticeSet set, long id, params (string Field, object? Value)[] values)
    {
        var record = new LatticeRecord(set.EntityType) { ["Id"] = id };

        foreach (var (field, value) in values)
        {
            record[field] = value;
        }

        return record;
    }

    // The ids of the records a search found, in ascending order: a search returns the entities of one key
    // in no order the contract states.
    public static long[] IdsOf(IEnumerable<LatticeRecord> found) => [.. found.Select(record => record.Get<long>("Id")).Order()];

    public static long[] IdsOf(IEnumerable<Account> found) => [.. found.Select(account => account.Id).Order()];

    // The write is refused with UniqueConstraintViolation, which names the index.
    public static async Task ExpectTakenAsync(string index, Func<ValueTask<LatticeWriteResult>> write)
    {
        var exception = await TestData.ExpectErrorAsync(LatticeErrorEnum.UniqueConstraintViolation, write);

        Assert.Equal(index, exception.IndexName);
    }
}
