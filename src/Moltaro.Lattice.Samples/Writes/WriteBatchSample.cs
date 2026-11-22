using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples.Writes;

// Several writes that have to succeed or fail together are submitted as one write batch.
// The master executes the whole batch at one position, or nothing of it; no reader sees
// a half. A batch may address different entity types, with classes and with records.
internal static class WriteBatchSample
{
    public static async Task RunAsync(LatticeSchema schema)
    {
        Console.WriteLine("== Write batch ==");

        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
        });

        var users = database.Set<User>();
        var settings = database.Set("Setting");

        // A new user and the settings that belong to it, together. Building the batch
        // touches nothing in the database; every entity is checked and copied as it is added.
        var onboarding = database.CreateWriteBatch();
        onboarding.Insert(new User { Id = 1, Name = "Ada", Department = "Research", Enabled = true });
        onboarding.Insert(new User { Id = 2, Name = "Grace", Department = "Research", Enabled = true });
        onboarding.Upsert(Setting(settings, "research.lead", "research", "lead", "1"));

        var saved = await onboarding.CommitAsync();
        Console.WriteLine($"Saved {onboarding.Count} entities at position {saved.Position}");

        // A change that depends on what was read: hand the lead over from Ada to Grace,
        // provided nobody has changed the setting in the meantime. Disabling Ada and
        // moving the lead happen together or not at all.
        users.TryGet(1, out var ada, out var adaRevision);
        settings.TryGet("research.lead", out var lead, out var leadRevision);

        ada!.Enabled = false;
        lead!["Value"] = "2";

        var handover = database.CreateWriteBatch();
        handover.Replace(ada, adaRevision);
        handover.Replace(lead, leadRevision);

        try
        {
            await handover.CommitAsync();
            Console.WriteLine($"Lead is now user {settings.Get("research.lead")!["Value"]}; Ada: {users.Get(1)}");
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.RevisionConflict)
        {
            // Nothing was written. Read again and decide again.
            Console.WriteLine($"Operation {exception.OperationIndex} was based on an older state; nothing was changed.");
        }

        // When one operation cannot be executed, the whole batch is refused.
        var conflicting = database.CreateWriteBatch();
        conflicting.Insert(new User { Id = 3, Name = "Linus", Enabled = true });
        conflicting.Insert(new User { Id = 2, Name = "Another Grace", Enabled = true });

        try
        {
            await conflicting.CommitAsync();
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.DuplicateKey)
        {
            Console.WriteLine($"Refused because of operation {exception.OperationIndex}; user 3 exists: {users.Get(3) is not null}");
        }

        Console.WriteLine();
    }

    private static LatticeRecord Setting(ILatticeSet settings, string id, string section, string name, string value) =>
        new(settings.EntityType)
        {
            ["Id"] = id,
            ["Section"] = section,
            ["Name"] = name,
            ["Value"] = value,
            ["Priority"] = 1,
        };
}
