namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal static class GeneratedScenarios
{
    public static List<ScenarioResult> Run()
    {
        var results = new List<ScenarioResult>();
        foreach (var reverseRoots in new[] { false, true })
        {
            foreach (var schedule in Interleavings(2, 5, ""))
            {
                using (var context = new ScenarioContext())
                {
                    var initial = new[] { Mutation.Put(ScenarioContext.A, 10, 1), Mutation.Put(ScenarioContext.B, 20, 2) };
                    if (reverseRoots) { Array.Reverse(initial); }
                    context.Write(initial);
                    context.Open();
                    var writes = new Mutation[][]
                    {
                        [Mutation.Add(ScenarioContext.A, 3)],
                        [Mutation.Remove(ScenarioContext.A)],
                        [Mutation.Put(ScenarioContext.A, 30, 4)],
                        [Mutation.Put(ScenarioContext.A, 40, 5), Mutation.Put(ScenarioContext.B, 50, 6)],
                        [Mutation.Set(ScenarioContext.A, 41)]
                    };
                    var writeIndex = 0;
                    foreach (var step in schedule)
                    {
                        if (step == 'C') { context.CopyNext(); }
                        else { context.Write(writes[writeIndex++]); }
                    }
                    context.Finish();
                }
                Check.That(NativeBlock.LiveBlocks == 0, "Native leak in interleaving schedule.");
                results.Add(new ScenarioResult($"Interleaving {schedule}, reverse={reverseRoots}", "Passed"));
            }
        }

        for (var seed = 0; seed < 64; seed++)
        {
            RunSeed(seed);
            Check.That(NativeBlock.LiveBlocks == 0, "Native leak in seeded history.");
            results.Add(new ScenarioResult($"Seeded history {seed}: 24 publications", "Passed"));
        }
        return results;
    }

    private static IEnumerable<string> Interleavings(int copies, int writes, string prefix)
    {
        if (copies == 0 && writes == 0)
        {
            yield return prefix;
            yield break;
        }
        if (copies > 0)
        {
            foreach (var schedule in Interleavings(copies - 1, writes, prefix + "C")) { yield return schedule; }
        }
        if (writes > 0)
        {
            foreach (var schedule in Interleavings(copies, writes - 1, prefix + "W")) { yield return schedule; }
        }
    }

    private static void RunSeed(int seed)
    {
        var random = new Random(20261113 + seed);
        using var context = new ScenarioContext();
        var keys = new[] { new RootKey(1, 1), new RootKey(1, 2), new RootKey(1, 3),
            new RootKey(2, 1), new RootKey(2, 2), new RootKey(2, 3) };
        context.Write(Mutation.Put(keys[0], 1), Mutation.Put(keys[1], 2), Mutation.Put(keys[3], 3));
        context.Open();
        for (var step = 0; step < 24; step++)
        {
            if (context.Captured < context.Start.SlotCount && random.Next(3) == 0) { context.CopyNext(); }
            var firstIndex = random.Next(keys.Length);
            var mutations = new List<Mutation> { NextMutation(keys[firstIndex], context.Reference.Values, random) };
            if (step % 5 == 0)
            {
                var secondIndex = (firstIndex + 1 + random.Next(keys.Length - 1)) % keys.Length;
                mutations.Add(NextMutation(keys[secondIndex], context.Reference.Values, random));
            }
            context.Write(mutations.ToArray());
            Check.Rejects(() => context.Replica.Observe(), "Seeded initialization became readable early.");
        }
        context.Finish();
    }

    private static Mutation NextMutation(RootKey key, IReadOnlyDictionary<RootKey, DocumentValue> current, Random random)
    {
        var number = random.Next(10000);
        if (!current.TryGetValue(key, out var value)) { return Mutation.Put(key, number, number + 1); }
        return random.Next(4) switch
        {
            0 => Mutation.Remove(key),
            1 when value.Items.Length < BlockCodec.MaximumItems => Mutation.Add(key, number),
            2 => Mutation.Put(key, number, number + 2),
            _ => Mutation.Set(key, number)
        };
    }
}
