using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples.Basics;

internal static class ViewSample
{
    public static async Task RunAsync(LatticeSchema schema)
    {
        Console.WriteLine("== A protected read by name ==");

        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
        });

        var settings = database.Set("Setting");
        var session = new LatticeSession();
        await settings.InsertAsync(new LatticeRecord(settings.EntityType)
        {
            ["Id"] = "ui.theme",
            ["Section"] = "ui",
            ["Name"] = "theme",
            ["Value"] = "dark",
            ["Priority"] = 10,
        }, session);

        if (settings.TryView("ui.theme", static view => view["Priority"].Get<int>(), out var priority))
        {
            Console.WriteLine($"Theme priority: {priority}");
        }

        // Resolve a field once for repeated reads. The result is an ordinary number
        // that can be kept after the synchronous viewer has returned.
        var valueField = settings.EntityType.GetField("Value");
        var answer = await settings.ViewAsync("ui.theme", session, valueField, static (view, field) => view[field].ByteLength);
        if (answer.Found)
        {
            Console.WriteLine($"Stored theme value: {answer.Result} bytes");
        }

        Console.WriteLine();
        Console.WriteLine("== A generated protected view ==");

        var users = database.Set<User>();
        await users.InsertAsync(new User { Id = 1, Name = "Ada", Enabled = true }, session);

        if (users.TryView(1L, static (UserView view) => view.Enabled, out var enabled))
        {
            Console.WriteLine($"Ada is enabled: {enabled}");
        }

        Console.WriteLine();
    }
}
