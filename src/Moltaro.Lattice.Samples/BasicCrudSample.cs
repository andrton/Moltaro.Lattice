using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples;

// Entities as objects of a .NET class: open, create, read, update, delete, enumerate, close.
internal static class BasicCrudSample
{
    public static async Task RunAsync(LatticeSchema schema)
    {
        Console.WriteLine("== CRUD with a .NET class ==");

        // Open. The database is ready when OpenAsync completes; "await using" closes it.
        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
        });

        // The schema knows which entity type the User class belongs to, its key and its fields.
        var users = database.Set<User>();

        // Create.
        await users.InsertAsync(new User { Id = 1, Name = "Ada", Department = "Research", Enabled = true });
        await users.InsertAsync(new User { Id = 2, Name = "Grace", Enabled = true });

        // Inserting an existing key fails and changes nothing.
        try
        {
            await users.InsertAsync(new User { Id = 1, Name = "Another Ada" });
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.DuplicateKey)
        {
            Console.WriteLine($"Insert refused: {exception.Message}");
        }

        // Read. The result is our own copy, together with the revision of that state.
        if (!users.TryGet(1, out var ada, out var revision))
        {
            throw new InvalidOperationException("User 1 must exist.");
        }

        Console.WriteLine($"Read: {ada}");

        // Update. Editing the copy changes nothing in the database until it is submitted.
        // The revision makes the write fail if somebody changed the user in between.
        ada.Department = "Engineering";
        await users.ReplaceAsync(ada, revision);

        // The same revision is now stale: a second write based on it is a conflict.
        try
        {
            ada.Department = "Sales";
            await users.ReplaceAsync(ada, revision);
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.RevisionConflict)
        {
            Console.WriteLine($"Stale update refused: {exception.Message}");
        }

        // Upsert: create or replace, whichever applies.
        await users.UpsertAsync(new User { Id = 3, Name = "Linus", Department = "Kernel", Enabled = false });

        // Enumerate all users.
        foreach (var user in users.Enumerate().OrderBy(user => user.Id))
        {
            Console.WriteLine($"Enumerated: {user}");
        }

        // Delete. An absent key is an ordinary result, not an error.
        Console.WriteLine($"Delete user 2: {(await users.DeleteAsync(2)).Changed}");
        Console.WriteLine($"Delete user 2 again: {(await users.DeleteAsync(2)).Changed}");
        Console.WriteLine($"Get user 2: {users.Get(2)?.ToString() ?? "null"}");
        Console.WriteLine();
    }
}
