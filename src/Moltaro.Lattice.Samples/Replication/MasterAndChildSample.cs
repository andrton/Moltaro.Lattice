using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples.Replication;

// A master and its read replica (child) in one process. Application code works with both
// through the same interface and does not care about the role.
internal static class MasterAndChildSample
{
    public static async Task RunAsync(LatticeSchema schema)
    {
        Console.WriteLine("== Master and child ==");

        // The master owns the data and accepts children at the address. Port 0 lets the system
        // choose a free port; the open master reports the address with that port in ListenUri.
        await using var master = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
            ListenUri = new Uri("lattice://127.0.0.1:0"),
        });

        // The child gets the schema and the whole database from the master at the address it
        // reports. It is readable as soon as OpenAsync completes; if it cannot reach its master,
        // or nothing arrives from it, for OpenTimeout, the open fails with Unavailable.
        await using var child = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = master.ListenUri,
            OpenTimeout = TimeSpan.FromSeconds(10),
        });

        // A write through the child is executed by the master and completes when this
        // child has applied it: the child reads its own write at once.
        await child.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada", Department = "Research", Enabled = true });
        Console.WriteLine($"Child reads its own write: {child.Set<User>().Get(1)}");

        // A write somewhere else, here on the master, reaches the child a moment later.
        // A session carries "what I have already seen" from one operation to the next:
        // the read below waits until the child has caught up with the write above it.
        var session = new LatticeSession();
        await master.Set<User>().InsertAsync(new User { Id = 2, Name = "Grace", Department = "Compilers", Enabled = true }, session);

        var grace = await child.Set<User>().GetAsync(2, session);
        Console.WriteLine($"Child reads the master's write through the session: {grace.Entity}");

        // The session travels as text, for example in a header, so that the next request
        // can continue from it on any node.
        Console.WriteLine($"Session position as text: {session.MinPosition}");
        var nextRequest = new LatticeSession(LatticePosition.Parse(session.MinPosition!.Value.ToString()));
        var listed = new List<User>();
        await foreach (var user in child.Set<User>().EnumerateAsync(nextRequest))
        {
            listed.Add(user);
        }

        Console.WriteLine($"Next request on the child sees: {string.Join("; ", listed.OrderBy(user => user.Id))}");

        // The master's rules apply to writes that come through a child.
        try
        {
            await child.Set<User>().InsertAsync(new User { Id = 1, Name = "Another Ada" });
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.DuplicateKey)
        {
            Console.WriteLine($"Insert through the child refused: {exception.Message}");
        }

        // When the master goes away, the child keeps serving what it has; writes are refused.
        await master.CloseAsync();
        Console.WriteLine($"Child after the master closed still has {child.Set<User>().Enumerate().Count()} users");

        try
        {
            await child.Set<User>().InsertAsync(new User { Id = 3, Name = "Nobody" });
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.Unavailable)
        {
            Console.WriteLine($"Insert through the child refused: {exception.Message}");
        }

        Console.WriteLine();
    }
}
