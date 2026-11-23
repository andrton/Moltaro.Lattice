using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples.Indexes;

// A unique index lets a value belong to one entity only. The schema declares it with
// "Unique": true; the master keeps the rule for every write, whichever node submits it.
//   Mailbox.ByAddress         (Address)          unique
//   Mailbox.ByAlias           (Alias)            unique; Alias may have no value
//   Account.ByTenantAndLogin  (Tenant, Login)    unique; Login is compared without case and may have no value
internal static class UniqueIndexSample
{
    private const string SchemaJson = """
        {
          "SchemaVersion": 1,
          "EntityTypes": [
            {
              "Id": 1,
              "Name": "Mailbox",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Address", "Type": "String" },
                { "Id": 3, "Name": "Alias", "Type": "String", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByAddress", "Fields": [2], "Unique": true },
                { "Id": 2, "Name": "ByAlias", "Fields": [3], "Unique": true }
              ]
            },
            {
              "Id": 2,
              "Name": "Account",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Tenant", "Type": "String" },
                { "Id": 3, "Name": "Login", "Type": "String", "Nullable": true, "Comparison": "OrdinalIgnoreCase" }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByTenantAndLogin", "Fields": [2, 3], "Unique": true }
              ]
            }
          ]
        }
        """;

    public static async Task RunAsync()
    {
        Console.WriteLine("== Unique index ==");

        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = LatticeSchema.Compile(SchemaJson),
        });

        var mailboxes = database.Set("Mailbox");
        await mailboxes.InsertAsync(Mailbox(mailboxes, 1, "ada@example.org", alias: null));

        // The address is taken: the second mailbox is refused, and the error names the index.
        try
        {
            await mailboxes.InsertAsync(Mailbox(mailboxes, 2, "ada@example.org", alias: null));
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.UniqueConstraintViolation)
        {
            Console.WriteLine($"Refused by the unique index {exception.IndexName}");
        }

        // "No value" is not a value: any number of mailboxes may have no alias.
        await mailboxes.InsertAsync(Mailbox(mailboxes, 2, "grace@example.org", alias: null));
        Console.WriteLine($"Mailboxes without an alias: {mailboxes.Enumerate().Count(mailbox => mailbox["Alias"] is null)}");

        // Two mailboxes exchange their addresses. Neither write would pass alone; one
        // batch is checked against the state it leaves.
        var ada = mailboxes.Get(1L)!;
        var grace = mailboxes.Get(2L)!;
        (ada["Address"], grace["Address"]) = (grace["Address"], ada["Address"]);

        var swap = database.CreateWriteBatch();
        swap.Replace(ada);
        swap.Replace(grace);
        await swap.CommitAsync();
        Console.WriteLine($"Mailbox 1 is now {mailboxes.Get(1L)!["Address"]}");

        await LoginsWithinTenantsAsync(database);
        Console.WriteLine();
    }

    // A unique index over two fields compares them together: a login belongs to one account
    // within its tenant, and each tenant has logins of its own.
    private static async Task LoginsWithinTenantsAsync(ILatticeDatabase database)
    {
        var accounts = database.Set("Account");

        // The same login in another tenant is another value of (Tenant, Login).
        await InsertOrReportAsync(accounts, Account(accounts, 1, "acme", "ada"));
        await InsertOrReportAsync(accounts, Account(accounts, 2, "globex", "ada"));

        // The same tenant and login is not, also when the login differs only in case:
        // the field compares its text without case.
        await InsertOrReportAsync(accounts, Account(accounts, 3, "acme", "ada"));
        await InsertOrReportAsync(accounts, Account(accounts, 3, "acme", "ADA"));

        // An account without a login is not compared: a tenant may have several.
        await InsertOrReportAsync(accounts, Account(accounts, 3, "acme", login: null));
        await InsertOrReportAsync(accounts, Account(accounts, 4, "acme", login: null));

        // The full key finds the one owner; the tenant alone finds all its accounts, those
        // without a login last and among themselves in no specified order.
        var byTenantAndLogin = accounts.Index("ByTenantAndLogin");
        var owner = byTenantAndLogin.Find(IndexKey.Equal("acme", "ada")).Single();
        Console.WriteLine($"Login ada of acme: account {owner["Id"]}");

        var ofAcme = byTenantAndLogin.Find(IndexKey.Equal("acme")).Select(account => account["Id"]);
        Console.WriteLine($"Accounts of acme: {string.Join(", ", ofAcme)}");
    }

    private static async Task InsertOrReportAsync(ILatticeSet accounts, LatticeRecord account)
    {
        var key = $"{account["Tenant"]}/{account["Login"] ?? "(no login)"}";

        try
        {
            await accounts.InsertAsync(account);
            Console.WriteLine($"Account {account["Id"]}, {key}: accepted");
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.UniqueConstraintViolation)
        {
            Console.WriteLine($"Account {account["Id"]}, {key}: refused by the unique index {exception.IndexName}");
        }
    }

    private static LatticeRecord Mailbox(ILatticeSet mailboxes, long id, string address, string? alias) =>
        new(mailboxes.EntityType)
        {
            ["Id"] = id,
            ["Address"] = address,
            ["Alias"] = alias,
        };

    private static LatticeRecord Account(ILatticeSet accounts, long id, string tenant, string? login) =>
        new(accounts.EntityType)
        {
            ["Id"] = id,
            ["Tenant"] = tenant,
            ["Login"] = login,
        };
}
