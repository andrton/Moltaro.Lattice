using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// The schema of the tests of unique indexes in the engine, of the master and of a child, and the classes
// of its entity types:
// - Account: the key Id, an Int64; Login, a String compared Ordinal, in the unique index ByLogin; Note, a
//   nullable String in no index, which a replace changes while it keeps its login; Data, a nullable
//   Binary in no index, which makes an entity as large as a test of memory or of a load needs.
// - Numbered: the key Id and Number, Int64 both, with the unique index ByNumber (Number), whose entries
//   are 16 bytes, so that a few thousand of them fill many leaves of nodes of 1 KiB.
internal static class UniqueSchema
{
    public static LatticeSchema Schema { get; } = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Account", "ClrType": "{{typeof(Account).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Login", "Type": "String" },
                { "Id": 3, "Name": "Note", "Type": "String", "Nullable": true },
                { "Id": 4, "Name": "Data", "Type": "Binary", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByLogin", "Fields": [2], "Unique": true } ]
            },
            {
              "Id": 2, "Name": "Numbered", "ClrType": "{{typeof(Numbered).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Number", "Type": "Int64" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByNumber", "Fields": [2], "Unique": true } ]
            }
          ]
        }
        """);

    public static Account Of(long id, string login, string? note = null, int? data = null) => new()
    {
        Id = id,
        Login = login,
        Note = note,
        Data = data is { } length ? new byte[length] : null,
    };

    // The ids of the accounts a search found, in ascending order.
    public static long[] IdsOf(IEnumerable<Account> found) => [.. found.Select(account => account.Id).Order()];

    // The ids of the accounts that hold the login, as a search of ByLogin finds them.
    public static long[] OwnersOf(ILatticeSet<Account> accounts, string login) => IdsOf(accounts.Index("ByLogin").Find(IndexKey.Equal(login)));

    // The write ends refused with UniqueConstraintViolation and the name of the index.
    public static async Task ExpectTakenAsync(string index, Task<LatticeWriteResult> write)
    {
        var refused = await Assert.ThrowsAsync<LatticeException>(() => write.WaitAsync(TestWait.Bound));

        Assert.Equal(LatticeErrorEnum.UniqueConstraintViolation, refused.Error);
        Assert.Equal(index, refused.IndexName);
    }

    public sealed class Account
    {
        public long Id { get; set; }

        public string Login { get; set; } = "";

        public string? Note { get; set; }

        public byte[]? Data { get; set; }
    }

    public sealed class Numbered
    {
        public long Id { get; set; }

        public long Number { get; set; }
    }
}
