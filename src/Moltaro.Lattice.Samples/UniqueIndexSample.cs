using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples;

// A unique index lets a value belong to one entity only. The schema declares it with
// "Unique": true; the master keeps the rule for every write, whichever node submits it.
//
// This shows the intended use. Unique indexes are not implemented yet: the first write
// below throws NotSupportedException, which Program.cs reports.
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

        Console.WriteLine();
    }

    private static LatticeRecord Mailbox(ILatticeSet mailboxes, long id, string address, string? alias) =>
        new(mailboxes.EntityType)
        {
            ["Id"] = id,
            ["Address"] = address,
            ["Alias"] = alias,
        };
}
