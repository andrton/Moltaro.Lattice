using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples.Schema;

// Text that repeats, such as the region of a customer, stored once for the whole database: the
// schema selects StringStorage Dictionary for the field. Nothing else changes for the
// application: the text reads back exactly as it was written, with its case, also on a field
// compared without case, and a field may be the key.
internal static class DictionaryStringsSample
{
    private const string SchemaJson = """
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Customer",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Name", "Type": "String" },
                { "Id": 3, "Name": "Region", "Type": "String", "Comparison": "OrdinalIgnoreCase", "StringStorage": "Dictionary" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    public static async Task RunAsync()
    {
        Console.WriteLine("== Dictionary strings ==");

        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = LatticeSchema.Compile(SchemaJson),
        });

        var customers = database.Set("Customer");
        (string Name, string Region)[] written = [("Ada", "North"), ("Grace", "South"), ("Linus", "North"), ("Barbara", "north")];

        for (var id = 0; id < written.Length; id++)
        {
            await customers.InsertAsync(new LatticeRecord(customers.EntityType)
            {
                ["Id"] = (long)id + 1,
                ["Name"] = written[id].Name,
                ["Region"] = written[id].Region,
            });
        }

        // North is stored once for Ada and Linus; north is a spelling of its own and comes back as
        // it was written, although the field compares without case.
        foreach (var customer in customers.Enumerate().OrderBy(customer => customer.Get<long>("Id")))
        {
            Console.WriteLine($"{customer["Name"]}: {customer["Region"]}");
        }

        Console.WriteLine();
    }
}
