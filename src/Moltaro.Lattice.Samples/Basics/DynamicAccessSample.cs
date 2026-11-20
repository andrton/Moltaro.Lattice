using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples.Basics;

// Entities by name, as records: no .NET class is involved.
internal static class DynamicAccessSample
{
    public static async Task RunAsync(LatticeSchema schema)
    {
        Console.WriteLine("== CRUD by name, without a .NET class ==");

        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
        });

        // "Setting" has no .NET class in the schema. It is fully usable by name.
        var settings = database.Set("Setting");

        // Create: a record holds field values by field name.
        var theme = new LatticeRecord(settings.EntityType)
        {
            ["Id"] = "ui.theme",
            ["Section"] = "ui",
            ["Name"] = "theme",
            ["Value"] = "dark",
            ["Priority"] = 10,
        };
        await settings.InsertAsync(theme);

        // Read and update, with the same revision check as for classes.
        if (settings.TryGet("ui.theme", out var stored, out var revision))
        {
            stored["Value"] = "light";
            await settings.ReplaceAsync(stored, revision);
        }

        // Enumerate without knowing the entity type in advance: the schema lists the fields.
        foreach (var record in settings.Enumerate())
        {
            var values = settings.EntityType.Fields.Select(field => $"{field.Name}={record[field.Name]}");
            Console.WriteLine($"Setting: {string.Join(", ", values)}");
        }

        // Delete.
        Console.WriteLine($"Delete setting 'ui.theme': {(await settings.DeleteAsync("ui.theme")).Changed}");

        // An entity type that has a .NET class is available both ways; it is the same data.
        await database.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada", Enabled = true });
        foreach (var record in database.Set("User").Enumerate())
        {
            Console.WriteLine($"User as a record: Id={record.Get<long>("Id")}, Name={record.Get<string>("Name")}, Department={record["Department"] ?? "null"}");
        }

        Console.WriteLine();
    }
}
