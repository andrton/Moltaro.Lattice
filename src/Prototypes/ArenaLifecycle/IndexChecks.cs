using Arc.Collections;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal static class IndexChecks
{
    public static string[] Run()
    {
        var passed = new List<string>();
        void Run(string name, Action check)
        {
            check();
            Check.That(NativeSegment.OutstandingBytes == 0, "Indexed scenario leaked native memory.");
            passed.Add(name);
        }

        Run("Primary/secondary updates, duplicate keys, abort and compaction", Lifecycle);
        Run("Old index candidates after update, deletion and same-key recreation", CandidateLifetime);
        Run("Equal-value replacement installs a new protected text descriptor", EqualValueReplacement);
        Run("Held index comparison excludes text reclamation and relocation", HeldComparison);
        Run("Prepared index publication has no managed allocation after warmup", PublicationAllocation);
        Run("String hash keys survive relocation with index-only ownership", HashTextLifetime);
        Run("Ordinal and OrdinalIgnoreCase agree with the CLR for Unicode", UnicodeKeys);
        return passed.ToArray();
    }

    public static void VerifyQueries(ArenaModel arena, ValueSpec?[] expected)
    {
        var claims = new RootClaim[ArenaModel.KeyLimit];
        var results = new ReadSummary[ArenaModel.KeyLimit];
        foreach (var group in Enumerable.Range(0, TextTable.Count))
        {
            var text = arena.Texts.Value(group);
            var count = arena.Query(text, text, long.MinValue, long.MaxValue, claims, results);
            var wanted = expected.Where(value => value is { } item &&
                string.Equals(arena.Texts.Value(item.TextGroup), text, arena.Texts.Comparison))
                .Select(value => value!.Value.Key).Order().ToArray();
            var actual = results.Take(count).Select(value => value.Key).Order().ToArray();
            Check.That(actual.SequenceEqual(wanted), "Indexed equality differs from logical oracle.");

            count = arena.Query(text, text, -10, 42, claims, results);
            var bounded = expected.Where(value => value is { } item && item.Number >= -10 && item.Number <= 42 &&
                string.Equals(arena.Texts.Value(item.TextGroup), text, arena.Texts.Comparison))
                .Select(value => value!.Value.Key).Order().ToArray();
            Check.That(results.Take(count).Select(value => value.Key).Order().SequenceEqual(bounded),
                "Indexed numeric range differs from logical oracle.");
        }

        var total = arena.Query(string.Empty, "\uffff", long.MinValue, long.MaxValue, claims, results);
        Check.That(total == expected.Count(value => value is not null), "Full range missed a root.");
        Check.That(results.Take(total).Select(value => value.Key).Distinct().Count() == total,
            "Full range returned duplicate roots.");
        for (var index = 1; index < total; index++)
        {
            var previous = results[index - 1];
            var next = results[index];
            var order = string.Compare(arena.Texts.Value(previous.TextGroup), arena.Texts.Value(next.TextGroup),
                arena.Texts.Comparison);
            Check.That(order < 0 || order == 0 && previous.Number <= next.Number, "Range order differs from CLR order.");
        }

        arena.Audit(expected);
    }

    private static void Lifecycle()
    {
        using var arena = new ArenaModel(true);
        var expected = new ValueSpec?[ArenaModel.KeyLimit];
        for (var key = 0; key < 384; key++)
        {
            expected[key] = ValueSpec.ForKey(key);
            arena.Save(expected[key]!.Value);
        }

        foreach (var key in new[] { 0, 1 })
        {
            expected[key] = expected[key]!.Value with { TextGroup = 7, Number = 42 };
            arena.Save(expected[key]!.Value);
        }

        VerifyQueries(arena, expected);
        using (var prepared = arena.Prepare(expected[0]!.Value with { TextGroup = 11 }))
        {
            Check.That(arena.IndexCount == 384 && arena.Read(0)!.Value.TextGroup == 7,
                "Private index preparation changed visibility.");
        }

        VerifyQueries(arena, expected);
        expected[0] = expected[0]!.Value with { TextGroup = 11, Number = -10 };
        arena.Save(expected[0]!.Value);
        arena.UpdateNumber(1, -20);
        expected[1] = expected[1]!.Value with { Number = -20 };
        VerifyQueries(arena, expected);
        for (var key = 0; key < 384; key++)
        {
            if (key % 4 != 0)
            {
                arena.Delete(key);
                expected[key] = null;
            }
        }

        VerifyQueries(arena, expected);
        var before = Enumerable.Range(0, 384).Select(arena.Read).ToArray();
        Check.That(arena.DrainCompaction() > 0, "Indexed arena did not compact.");
        var memory = arena.Allocator.Snapshot();
        Check.That(memory.RootMoves > 0 && memory.ChunkMoves > 0 && memory.TextMoves > 0,
            "All indexed ownership paths must move.");
        VerifyQueries(arena, expected);
        for (var key = 0; key < 384; key++)
        {
            Check.That(before[key] == arena.Read(key), "Indexed relocation changed revision/data.");
        }

        foreach (var key in Enumerable.Range(0, 384))
        {
            arena.Delete(key);
            expected[key] = null;
        }

        VerifyQueries(arena, expected);
        Check.That(arena.Allocator.Snapshot().LiveBlocks == 0, "Indexes retained deleted text.");
    }

    private static void CandidateLifetime()
    {
        using var arena = new ArenaModel(true);
        var expected = new ValueSpec?[ArenaModel.KeyLimit];
        expected[4] = ValueSpec.ForKey(4);
        arena.Save(expected[4]!.Value);
        var claims = new RootClaim[8];
        var results = new ReadSummary[8];
        var text = TextTable.Expected(4);
        var count = arena.CaptureCandidates(text, text, long.MinValue, long.MaxValue, claims);
        Check.That(count == 1, "Missing candidate.");
        expected[4] = expected[4]!.Value with { TextGroup = 9 };
        arena.Save(expected[4]!.Value);
        Check.That(arena.ReadCandidates(claims.AsSpan(0, count), text, text, long.MinValue, long.MaxValue, results) == 0,
            "Stale candidate escaped predicate revalidation.");

        text = TextTable.Expected(9);
        count = arena.CaptureCandidates(text, text, long.MinValue, long.MaxValue, claims);
        arena.Delete(4);
        arena.Save(expected[4]!.Value);
        Check.That(arena.ReadCandidates(claims.AsSpan(0, count), text, text, long.MinValue, long.MaxValue, results) == 0,
            "Old candidate redirected to a recreated key.");
        VerifyQueries(arena, expected);
    }

    private static void EqualValueReplacement()
    {
        using var arena = new ArenaModel(true, StringComparison.OrdinalIgnoreCase, true);
        var expected = new ValueSpec?[ArenaModel.KeyLimit];
        for (var pass = 0; pass < 8; pass++)
        {
            var group = 248 + pass % 2;
            expected[0] = ValueSpec.ForKey(0) with { TextGroup = group, Number = 7 };
            arena.Save(expected[0]!.Value);
            arena.DrainCompaction();
            VerifyQueries(arena, expected);
            var text = arena.Allocator.Allocations().Single(value => value.Owner.Kind == BlockKindEnum.Text);
            Check.That(text.Owner.Entry == group, "Equal-key update retained obsolete text storage.");
        }

        // Equal logical keys can own different spellings/allocations. Removing
        // the duplicate-group head must retain the promoted node's own text.
        expected[1] = ValueSpec.ForKey(1) with { TextGroup = 248, Number = 7 };
        expected[2] = ValueSpec.ForKey(2) with { TextGroup = 249, Number = 7 };
        arena.Save(expected[1]!.Value);
        arena.Save(expected[2]!.Value);
        VerifyQueries(arena, expected);
        arena.Delete(0);
        expected[0] = null;
        VerifyQueries(arena, expected);
        arena.Delete(2);
        expected[2] = null;
        VerifyQueries(arena, expected);
        var remainingText = arena.Allocator.Allocations().Single(value => value.Owner.Kind == BlockKindEnum.Text);
        Check.That(remainingText.Owner.Entry == 248, "Duplicate-head promotion retained the removed spelling.");
    }

    private static void HeldComparison()
    {
        using var arena = new ArenaModel(true);
        var expected = new ValueSpec?[ArenaModel.KeyLimit];
        for (var key = 0; key < 160; key++)
        {
            expected[key] = ValueSpec.ForKey(key);
            arena.Save(expected[key]!.Value);
        }

        using var prepared = arena.Prepare(expected[130]!.Value with { TextGroup = 200 });
        arena.Delete(0);
        expected[0] = null;
        var candidate = arena.Allocator.Allocations().Single(value =>
            value.Owner.Kind == BlockKindEnum.Text && value.Owner.Entry == 129);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var reader = new TestThread(() => arena.HeldIndexComparisonForTest(() =>
        {
            entered.Set();
            Check.That(release.Wait(TimeSpan.FromSeconds(3)), "Held comparison timed out.");
        }));
        TestThread? writer = null;
        try
        {
            Check.That(entered.Wait(TimeSpan.FromSeconds(2)), "Index comparison did not enter.");
            Check.That(!arena.TryMove(candidate), "Compactor moved text under its comparison reader.");
            writer = new TestThread(() => arena.Publish(prepared));
            TestThread.Await(() => arena.WaitingTextWriters > 0, "Publisher did not wait for protected text.");
        }
        finally
        {
            release.Set();
            writer?.Dispose();
            reader.Dispose();
        }

        expected[130] = expected[130]!.Value with { TextGroup = 200 };
        Check.That(arena.TryMove(candidate), "Text could not move after the reader released it.");
        VerifyQueries(arena, expected);
    }

    private static void PublicationAllocation()
    {
        using var arena = new ArenaModel(true);
        for (var key = 0; key < 160; key++)
        {
            arena.Save(ValueSpec.ForKey(key));
        }

        // Exercise both detached insert nodes and existing-node replacement.
        for (var pass = 0; pass < 16; pass++)
        {
            var key = pass % 2 == 0 ? pass : 200 + pass;
            using var prepared = arena.Prepare(ValueSpec.ForKey(key, 1) with { TextGroup = 17 + pass });
            var before = GC.GetAllocatedBytesForCurrentThread();
            arena.Publish(prepared);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check.That(bytes == 0, $"Prepared indexed publication allocated {bytes} bytes.");
        }
    }

    private static void HashTextLifetime()
    {
        using var allocator = new ArenaAllocator();
        using var texts = new TextTable(allocator);
        var map = new UnorderedMapSlim<NativeTextKey, int>(256);
        var tokens = new TextToken[TextTable.Count];
        var expectedIndex = new int[TextTable.Count];
        for (var group = 0; group < TextTable.Count; group++)
        {
            var token = texts.Prepare(group);
            tokens[group] = token;
            texts.RetainIndex(token);
            Check.That(map.TryAdd(new NativeTextKey(texts, token), group), "Duplicate string primary key.");
            texts.Release(token, true);
            expectedIndex[group] = 1;
        }

        for (var group = 0; group < TextTable.Count; group++)
        {
            if (group % 4 != 0)
            {
                Check.That(map.Remove(new NativeTextKey(texts, default, TextTable.Expected(group))), "String key removal failed.");
                texts.ReleaseIndex(tokens[group]);
                expectedIndex[group] = 0;
            }
        }

        var moved = 0;
        while (allocator.TryCandidate(out var candidate))
        {
            Check.That(++moved < 256 && texts.TryMove(candidate), "String-only compaction failed.");
        }

        Check.That(moved > 0, "String hash keys did not exercise relocation.");
        for (var group = 0; group < TextTable.Count; group++)
        {
            var found = map.TryGetValue(new NativeTextKey(texts, default, TextTable.Expected(group)), out var value);
            Check.That(found == (expectedIndex[group] == 1) && (!found || value == group), "Hash lookup changed after relocation.");
        }

        var owners = new Dictionary<BlockAddress, BlockOwner>();
        texts.Audit(new int[TextTable.Count], owners, expectedIndex);
        Check.That(owners.Count == allocator.Allocations().Length, "String index ownership mismatch.");
        var stale = new NativeTextKey(texts, tokens[0]);
        Check.That(map.Remove(stale), "Final hash removal failed.");
        texts.ReleaseIndex(tokens[0]);
        var detected = false;
        try
        {
            stale.GetHashCode();
        }
        catch (InvalidOperationException exception) when (exception.Message == "Stale text lifetime.")
        {
            detected = true;
        }

        Check.That(detected, "Stale descriptor was not rejected before dereference.");
        var recreated = texts.Prepare(0);
        Check.That(recreated.Generation != tokens[0].Generation, "Freed text generation reused.");
        texts.RetainIndex(recreated);
        Check.That(map.TryAdd(new NativeTextKey(texts, recreated), 0), "Recreated string key insertion failed.");
        texts.Release(recreated, true);
        foreach (var pair in map.ToArray())
        {
            map.Remove(pair.Key);
            texts.ReleaseIndex(pair.Key.Token);
        }

        Check.That(allocator.Snapshot().LiveBlocks == 0, "String index did not release its final owners.");
    }

    private static void UnicodeKeys()
    {
        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
        {
            using var allocator = new ArenaAllocator();
            using var texts = new TextTable(allocator, comparison, true);
            var keys = new NativeTextKey[8];
            for (var index = 0; index < keys.Length; index++)
            {
                keys[index] = new NativeTextKey(texts, texts.Prepare(248 + index));
            }

            foreach (var left in keys)
            {
                foreach (var right in keys)
                {
                    var wanted = string.Compare(texts.Value(left.Token.Entry), texts.Value(right.Token.Entry), comparison);
                    Check.That(Math.Sign(left.CompareTo(right)) == Math.Sign(wanted), "Unicode order mismatch.");
                    Check.That(left.Equals(right) == (wanted == 0), "Unicode equality mismatch.");
                    if (wanted == 0)
                    {
                        Check.That(left.GetHashCode() == right.GetHashCode(), "Equivalent text hashes differ.");
                    }
                }
            }

            var map = new UnorderedMapSlim<NativeTextKey, int>(16);
            Check.That(map.TryAdd(keys[0], 1), "Initial case key missing.");
            Check.That(map.TryAdd(keys[1], 2) == (comparison == StringComparison.Ordinal), "Primary case identity mismatch.");
            map.Clear();
            foreach (var key in keys)
            {
                texts.Release(key.Token, true);
            }
        }
    }
}
