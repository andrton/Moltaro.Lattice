namespace Moltaro.Lattice.Prototypes.OperationRetry;

internal static class UniqueIndexChecks
{
    public static string[] Run()
    {
        var alice = new UniqueKey("alice", "tenant-1");
        var bob = new UniqueKey("bob", "tenant-1");
        var index = new UniqueIndexFixture();
        Require(index.TryApply(new UniqueUpdate(1, alice), new UniqueUpdate(2, bob)));
        Require(!index.TryApply(new UniqueUpdate(3, alice)));
        Require(index.Read(3) is null);

        Require(index.TryApply(new UniqueUpdate(1, bob), new UniqueUpdate(2, alice)));
        Require(index.Read(1) == bob && index.Read(2) == alice);
        Require(!index.TryApply(new UniqueUpdate(1, bob), new UniqueUpdate(2, bob)));
        Require(index.Read(1) == bob && index.Read(2) == alice);
        Require(!index.TryApply(new UniqueUpdate(1, alice), new UniqueUpdate(3, new UniqueKey("new", "tenant-1"))));
        Require(index.Read(1) == bob && index.Read(3) is null);

        Require(index.TryApply(new UniqueUpdate(1, null), new UniqueUpdate(3, bob)));
        Require(index.Read(1) is null && index.Read(3) == bob);
        Require(index.TryApply(new UniqueUpdate(4, new UniqueKey(null, "tenant-1")), new UniqueUpdate(5, new UniqueKey(null, "tenant-1"))));
        Require(index.TryApply(new UniqueUpdate(6, new UniqueKey("alice", null)), new UniqueUpdate(7, new UniqueKey("alice", null))));
        Require(index.TryApply(new UniqueUpdate(8, new UniqueKey("alice", "tenant-2"))));

        var insensitive = new UniqueIndexFixture(StringComparer.OrdinalIgnoreCase);
        Require(insensitive.TryApply(new UniqueUpdate(1, alice)));
        Require(!insensitive.TryApply(new UniqueUpdate(2, new UniqueKey("ALICE", "TENANT-1"))));

        var concurrent = new UniqueIndexFixture();
        using var start = new ManualResetEventSlim();
        var first = Task.Run(() =>
        {
            Require(start.Wait(TimeSpan.FromSeconds(2)));
            return concurrent.TryApply(new UniqueUpdate(1, alice));
        });
        var second = Task.Run(() =>
        {
            Require(start.Wait(TimeSpan.FromSeconds(2)));
            return concurrent.TryApply(new UniqueUpdate(2, alice));
        });
        start.Set();
        Task.WaitAll(first, second);
        Require(first.Result != second.Result);

        return [
            "duplicate insertion rejected",
            "atomic swap accepted against final state",
            "within-batch collision leaves old state intact",
            "outside-owner conflict rejects every batch member",
            "delete and reclaim in one batch accepted",
            "any-null composite keys allow duplicates",
            "composite scope distinguishes equal first fields",
            "schema comparer controls uniqueness",
            "concurrent different roots produce exactly one winner"
        ];
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Unique-index contract check failed.");
        }
    }
}
