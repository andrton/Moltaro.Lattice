namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal static class DirectedChecks
{
    public static string[] Run()
    {
        var passed = new List<string>();
        void Run(string name, Action scenario)
        {
            scenario();
            Check.That(NativeSegment.OutstandingBytes == 0, "Native allocation leaked after scenario.");
            passed.Add(name);
        }

        Run("Detached copy, shared text, staged abort and scalar update", CopyAndPreparation);
        Run("Delete, same-key recreation, old claim and stale move", DeleteAndRecreate);
        Run("Held root reader blocks writer and compaction skips", HeldRoot);
        Run("Shared text reader blocks another root's reference release", HeldText);
        Run("Prepared text claim survives removal of the last stored reference", PreparedText);
        Run("Allocation refusal before publication preserves all prior roots", Exhaustion);
        Run("Negative controls detect torn fields and corrupt payload", NegativeControls);
        return passed.ToArray();
    }

    private static void CopyAndPreparation()
    {
        using var arena = new ArenaModel();
        var oracle = new ValueSpec?[ArenaModel.KeyLimit];
        oracle[0] = ValueSpec.ForKey(0) with { TextGroup = 7 };
        oracle[1] = ValueSpec.ForKey(1) with { TextGroup = 7 };
        arena.Save(oracle[0]!.Value);
        arena.Save(oracle[1]!.Value);
        var copy = arena.Copy(0)!;
        var originalBytes = copy.Payload.ToArray();
        var before = arena.Read(0);
        using (var prepared = arena.Prepare(ValueSpec.ForKey(0, 1)))
        {
            Check.That(arena.Read(0) == before, "Preparation changed published state.");
            Check.That(arena.Allocator.Snapshot().PreparedCapacityBytes > 0, "Missing prepared memory accounting.");
        }

        arena.Audit(oracle);
        var rootBefore = arena.Allocator.Allocations().Single(value =>
            value.Owner.Kind == BlockKindEnum.Root && value.Owner.Entry == 0).Address;
        arena.UpdateNumber(0, 999);
        oracle[0] = oracle[0]!.Value with { Number = 999 };
        var rootAfter = arena.Allocator.Allocations().Single(value =>
            value.Owner.Kind == BlockKindEnum.Root && value.Owner.Entry == 0).Address;
        Check.That(rootBefore == rootAfter, "Scalar update copied the root.");
        oracle[0] = ValueSpec.ForKey(0, 2);
        using (var prepared = arena.Prepare(oracle[0]!.Value))
        {
            Check.That(arena.Read(0)!.Value.Number == 999, "Old value unavailable before publication.");
            arena.Publish(prepared);
        }

        Check.That(copy.Payload.SequenceEqual(originalBytes) && copy.Text == TextTable.Expected(7) &&
            copy.Summary == before, "Detached copy changed after update.");
        Check.That(arena.Delete(0) && !arena.Delete(0), "Delete result.");
        oracle[0] = null;
        Check.That(arena.Read(1)!.Value.TextGroup == 7, "Shared text freed too early.");
        arena.Audit(oracle);
    }

    private static void DeleteAndRecreate()
    {
        using var arena = new ArenaModel();
        var oracle = new ValueSpec?[ArenaModel.KeyLimit];
        arena.Save(ValueSpec.ForKey(3));
        var oldClaim = arena.Pin(3)!.Value;
        var stale = arena.Allocator.Allocations().First(value => value.Owner.Kind == BlockKindEnum.Root);
        Check.That(arena.Delete(3), "Delete existing root.");
        oracle[3] = ValueSpec.ForKey(3, 1);
        arena.Save(oracle[3]!.Value);
        Check.That(arena.ReadClaim(oldClaim) is null, "Old lookup redirected to new lifetime.");
        Check.That(!arena.TryMove(stale), "Stale compaction candidate moved replacement root.");
        var newClaim = arena.Pin(3)!.Value;
        Check.That(newClaim != oldClaim, "Retained control entry reused.");
        arena.Unpin(newClaim);
        arena.Unpin(oldClaim);
        arena.Delete(3);
        oracle[3] = ValueSpec.ForKey(3, 2);
        arena.Save(oracle[3]!.Value);
        Check.That(!arena.TryMove(stale), "Reused control slot accepted obsolete generation.");
        arena.Audit(oracle);
    }

    private static void HeldRoot()
    {
        using var arena = new ArenaModel();
        var oracle = new ValueSpec?[ArenaModel.KeyLimit];
        for (var key = 0; key < 160; key++)
        {
            oracle[key] = ValueSpec.ForKey(key);
            arena.Save(oracle[key]!.Value);
        }

        arena.Delete(0);
        oracle[0] = null;
        var claim = arena.Pin(129)!.Value;
        var candidate = arena.Allocator.Allocations().Single(value =>
            value.Owner.Kind == BlockKindEnum.Root && value.Owner.Entry == claim.Entry);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var reader = new TestThread(() => arena.HeldReadForTest(129, () =>
        {
            entered.Set();
            Check.That(release.Wait(TimeSpan.FromSeconds(3)), "Held reader timed out.");
        }));
        TestThread? writer = null;
        try
        {
            Check.That(entered.Wait(TimeSpan.FromSeconds(2)), "Reader did not enter.");
            Check.That(!arena.TryMove(candidate), "Compactor did not skip held root.");
            writer = new TestThread(() => Check.That(arena.UpdateNumber(129, 12345), "Writer lost root."));
            TestThread.Await(() => arena.WaitingRootWriters(claim) > 0, "Writer did not wait for reader.");
        }
        finally
        {
            release.Set();
            writer?.Dispose();
            reader.Dispose();
            arena.Unpin(claim);
        }

        oracle[129] = oracle[129]!.Value with { Number = 12345 };
        var before = arena.Read(129);
        Check.That(arena.TryMove(candidate), "Released root was not movable into an existing hole.");
        Check.That(arena.Read(129) == before, "Physical move changed value/revision.");
        arena.Audit(oracle);
    }

    private static void HeldText()
    {
        using var arena = new ArenaModel();
        var oracle = new ValueSpec?[ArenaModel.KeyLimit];
        oracle[0] = ValueSpec.ForKey(0) with { TextGroup = 9 };
        oracle[1] = ValueSpec.ForKey(1) with { TextGroup = 9 };
        arena.Save(oracle[0]!.Value);
        arena.Save(oracle[1]!.Value);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var reader = new TestThread(() => arena.HeldReadForTest(1, () =>
        {
            entered.Set();
            Check.That(release.Wait(TimeSpan.FromSeconds(3)), "Text reader timed out.");
        }, true));
        TestThread? writer = null;
        try
        {
            Check.That(entered.Wait(TimeSpan.FromSeconds(2)), "Text reader did not enter.");
            writer = new TestThread(() => Check.That(arena.Delete(0), "Delete failed."));
            TestThread.Await(() => arena.WaitingTextWriters > 0, "Shared text protection did not block release.");
        }
        finally
        {
            release.Set();
            writer?.Dispose();
            reader.Dispose();
        }

        oracle[0] = null;
        arena.Audit(oracle);
    }

    private static void Exhaustion()
    {
        using var arena = new ArenaModel();
        var oracle = new ValueSpec?[ArenaModel.KeyLimit];
        var refused = false;
        for (var key = 0; key < ArenaModel.KeyLimit; key++)
        {
            var value = ValueSpec.ForKey(key) with { Length = 2048 };
            try
            {
                arena.Save(value);
                oracle[key] = value;
            }
            catch (OutOfMemoryException)
            {
                refused = true;
                Check.That(arena.Read(key) is null, "Rejected insert became visible.");
                break;
            }
        }

        Check.That(refused, "The native budget was not enforced.");
        var previous = arena.Read(0);
        var replacementRefused = false;
        try
        {
            using var prepared = arena.Prepare(oracle[0]!.Value with { Number = 123, Seed = 456 });
        }
        catch (OutOfMemoryException)
        {
            replacementRefused = true;
        }

        Check.That(replacementRefused && arena.Read(0) == previous,
            "Refused replacement did not preserve the existing root.");
        arena.Audit(oracle);
    }

    private static void PreparedText()
    {
        using var arena = new ArenaModel();
        var oracle = new ValueSpec?[ArenaModel.KeyLimit];
        arena.Save(ValueSpec.ForKey(0) with { TextGroup = 17 });
        oracle[2] = ValueSpec.ForKey(2) with { TextGroup = 17 };
        using var prepared = arena.Prepare(oracle[2]!.Value);
        arena.Delete(0);
        Check.That(arena.Read(2) is null, "Prepared insert became visible before publication.");
        Check.That(arena.Allocator.Allocations().Count(value => value.Owner.Kind == BlockKindEnum.Text) == 1,
            "Prepared string claim did not retain its native text.");
        arena.Publish(prepared);
        arena.Audit(oracle);
    }

    private static void NegativeControls()
    {
        using var arena = new ArenaModel();
        arena.Save(ValueSpec.ForKey(0));
        var allocations = arena.Allocator.Allocations();
        var root = allocations.Single(value => value.Owner.Kind == BlockKindEnum.Root);
        var bytes = arena.Allocator.Span(root.Address, RootCodec.Bytes);
        var inverse = RootCodec.Long(bytes, 16);
        RootCodec.Long(bytes, 16, 123);
        ExpectCorruption(arena);
        RootCodec.Long(bytes, 16, inverse);
        var chunk = allocations.First(value => value.Owner.Kind == BlockKindEnum.Chunk);
        var payload = arena.Allocator.Span(chunk.Address, chunk.Length);
        var previous = payload[0];
        payload[0] ^= 1;
        ExpectCorruption(arena);
        payload[0] = previous;
        Check.That(arena.Read(0) is not null, "Negative control repair failed.");
    }

    private static void ExpectCorruption(ArenaModel arena)
    {
        var detected = false;
        try
        {
            arena.Read(0);
        }
        catch (InvalidOperationException exception) when (
            exception.Message is "Torn root fields." or "Mixed or corrupt payload.")
        {
            detected = true;
        }

        Check.That(detected, "Negative control was not detected.");
    }
}
