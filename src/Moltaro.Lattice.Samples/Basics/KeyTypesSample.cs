using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples.Basics;

// A primary key is one of the eight integer types, a String or a Guid, as the schema declares it.
// A key is given as that type, or as an integer of another width when it fits. Anything else is
// refused as an argument: a number that does not fit, and a text for a Guid key, which is never
// parsed.
internal static class KeyTypesSample
{
    private static readonly string SchemaJson = $$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Device", "ClrType": "{{typeof(Device).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Guid" }, { "Id": 2, "Name": "Name", "Type": "String" } ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Port",
              "Fields": [ { "Id": 1, "Name": "Number", "Type": "UInt16" }, { "Id": 2, "Name": "Service", "Type": "String" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    public static async Task RunAsync()
    {
        Console.WriteLine("== Key types ==");

        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = LatticeSchema.Compile(SchemaJson),
        });

        // A Guid key, through a class. Its text is not a key.
        var devices = database.Set<Device>();
        var printer = Guid.Parse("5f0c1b8e-58a4-4c4e-9a43-2f6f1c0d7e11");
        await devices.InsertAsync(new Device { Id = printer, Name = "Printer" });
        Console.WriteLine($"Device {printer}: {devices.Get(printer)?.Name}");

        try
        {
            devices.Get(printer.ToString());
        }
        catch (ArgumentException exception)
        {
            Console.WriteLine($"Read by its text refused: {exception.Message}");
        }

        // A UInt16 key, by name. A literal Int32 is accepted when it fits; a negative one does not.
        var ports = database.Set("Port");
        await ports.InsertAsync(new LatticeRecord(ports.EntityType) { ["Number"] = 443, ["Service"] = "https" });
        Console.WriteLine($"Port 443: {ports.Get(443)?["Service"]}");

        try
        {
            ports.Get(-1);
        }
        catch (ArgumentException exception)
        {
            Console.WriteLine($"Read by -1 refused: {exception.Message}");
        }

        Console.WriteLine();
    }

    private sealed class Device
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";
    }
}
