using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples.Indexes;

// Finding entities through the secondary indexes declared in the schema:
//   User.ByEmail              (Email)
//   User.ByDepartmentAndName  (Department, Name)
//   Setting.BySectionAndName  (Section, Name)
// The application names the index it wants; there is no query language behind this. A search
// finds by the full key of an index, by its leading fields, with a range on the next field, in
// reverse and in pages, and returns each entity at most once. A value of a key has the type of its
// field, or is the same number in another numeric type; an Enum field is given as its integer.
internal static class IndexSample
{
    // Orders with a Decimal total and an Enum status, each indexed, in a database of their own.
    private const string OrderSchema = """
        {
          "Enums": [ { "Id": 1, "Name": "Status", "Members": [ { "Name": "Open", "Value": 1 }, { "Name": "Shipped", "Value": 2 } ] } ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Order",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Total", "Type": "Decimal" },
                { "Id": 3, "Name": "Status", "Type": "Enum", "Enum": "Status" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByTotal", "Fields": [2] }, { "Id": 2, "Name": "ByStatus", "Fields": [3] } ]
            }
          ]
        }
        """;

    public static async Task RunAsync(LatticeSchema schema)
    {
        Console.WriteLine("== Finding by index ==");

        var address = new Uri("lattice://localhost:7401");
        await using var master = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
            ListenUri = address,
        });

        var users = master.Set<User>();
        await users.InsertAsync(new User { Id = 1, Name = "Ada", Department = "Research", Email = "ada@example.org", Enabled = true });
        await users.InsertAsync(new User { Id = 2, Name = "Grace", Department = "Research", Email = "grace@example.org", Enabled = true });
        await users.InsertAsync(new User { Id = 3, Name = "Linus", Department = "Kernel", Email = "linus@example.org", Enabled = true });
        await users.InsertAsync(new User { Id = 4, Name = "Barbara", Department = "Research", Email = "barbara@example.org", Enabled = false });
        await users.InsertAsync(new User { Id = 5, Name = "Margaret", Department = "Research", Email = "margaret@example.org", Enabled = true });

        // An index on one field. The index object can be kept and reused.
        var byEmail = users.Index("ByEmail");
        foreach (var user in byEmail.Find(IndexKey.Equal("ada@example.org")))
        {
            Console.WriteLine($"By email: {user}");
        }

        // An index on two fields, with values for both.
        var byDepartmentAndName = users.Index("ByDepartmentAndName");
        foreach (var user in byDepartmentAndName.Find(IndexKey.Equal("Kernel", "Linus")))
        {
            Console.WriteLine($"By department and name: {user}");
        }

        // The first field only: the whole department, in the order of the second field.
        var research = byDepartmentAndName.Find(IndexKey.Equal("Research")).Select(user => user.Name);
        Console.WriteLine($"Research, by name: {string.Join(", ", research)}");

        // The first field and a range of the second: names from "B" up to, not including, "H".
        var fromBToG = byDepartmentAndName
            .Find(IndexKey.Equal("Research").Range(IndexBound.Inclusive("B"), IndexBound.Exclusive("H")))
            .Select(user => user.Name);
        Console.WriteLine($"Research, names B to G: {string.Join(", ", fromBToG)}");

        // Options: the reverse order and a page of two.
        var page = await byDepartmentAndName.FindAsync(
            IndexKey.Equal("Research"),
            new LatticeFindOptions { Reverse = true, Limit = 2 });
        Console.WriteLine($"Research, last two by name: {string.Join(", ", page.Items.Select(user => user.Name))}; more: {page.HasMore}");

        // The next page: Offset skips the two already shown. Each page is a search of its own, so a
        // write between two pages can shift what a later page shows.
        var nextPage = await byDepartmentAndName.FindAsync(
            IndexKey.Equal("Research"),
            new LatticeFindOptions { Reverse = true, Offset = 2, Limit = 2 });
        Console.WriteLine($"Research, the two before them: {string.Join(", ", nextPage.Items.Select(user => user.Name))}; more: {nextPage.HasMore}");

        // Records are found the same way, without a class.
        var settings = master.Set("Setting");
        await settings.InsertAsync(new LatticeRecord(settings.EntityType)
        {
            ["Id"] = "mail.sender",
            ["Section"] = "mail",
            ["Name"] = "sender",
            ["Value"] = "noreply@example.org",
            ["Priority"] = 1,
        });

        var found = await settings.Index("BySectionAndName").FindAsync(IndexKey.Equal("mail", "sender"), new LatticeFindOptions());
        Console.WriteLine($"Setting mail/sender: {found.Items[0]["Value"]}");

        await SearchOnReplicaAsync(master, address);
        await SearchByNumberAndEnumAsync();
        Console.WriteLine();
    }

    // A number of another type is accepted when it is the same number in the type of the field: an
    // integer for a Decimal. An Enum field is searched by the integer of its member.
    private static async Task SearchByNumberAndEnumAsync()
    {
        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = LatticeSchema.Compile(OrderSchema),
        });

        var orders = database.Set("Order");
        await orders.InsertAsync(new LatticeRecord(orders.EntityType) { ["Id"] = 1L, ["Total"] = 25.00m, ["Status"] = 1 });
        await orders.InsertAsync(new LatticeRecord(orders.EntityType) { ["Id"] = 2L, ["Total"] = 40.50m, ["Status"] = 2 });

        var totalOf25 = orders.Index("ByTotal").Find(IndexKey.Equal(25)).Single();
        Console.WriteLine($"Order with a total of 25, given as an Int32: #{totalOf25["Id"]}, {totalOf25["Total"]}");

        var shipped = orders.Index("ByStatus").Find(IndexKey.Equal(2)).Select(order => order["Id"]);
        Console.WriteLine($"Shipped orders, by the integer of the member: {string.Join(", ", shipped)}");
    }

    // On a replica the indexes follow the master with a small delay. Two ways to search
    // an index that is guaranteed to include a particular write.
    private static async Task SearchOnReplicaAsync(ILatticeDatabase master, Uri address)
    {
        await using var child = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = address,
        });

        var childByEmail = child.Set<User>().Index("ByEmail");

        // 1. Wait once for the position of the write, then read as often as needed.
        var written = await master.Set<User>().InsertAsync(new User { Id = 6, Name = "Edsger", Email = "edsger@example.org", Enabled = true });
        await child.WaitForPositionAsync(written.Position);
        Console.WriteLine($"Replica after waiting for the position: {childByEmail.Find(IndexKey.Equal("edsger@example.org")).Single()}");

        // 2. Give the session to the search itself; it waits when it has to.
        var session = new LatticeSession();
        await master.Set<User>().InsertAsync(new User { Id = 7, Name = "Tim", Email = "tim@example.org", Enabled = true }, session);
        var result = await childByEmail.FindAsync(IndexKey.Equal("tim@example.org"), new LatticeFindOptions { Session = session });
        Console.WriteLine($"Replica through the session: {result.Items[0]}");
    }
}
