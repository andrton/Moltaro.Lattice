using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal static class UnitFixture
{
    public static Guid Token(int cycle) => new(200 + cycle, 0, 0, new byte[8]);

    public static BatchMutation[] Changes(int cycle)
    {
        var changes = BatchFixture.Changes(8, cycle);
        changes[0] = changes[0] with { Value = changes[0].Value with { Length = cycle % 2 == 0 ? 128 : 1536 } };
        changes[1] = cycle % 2 == 0 ? changes[1] : BatchMutation.Delete(changes[1].Identity);
        changes[2] = cycle % 2 == 0 ? BatchMutation.Delete(changes[2].Identity) : changes[2];
        changes[3] = cycle % 2 == 0 ? BatchMutation.Delete(new EntityKey(EntityTypeEnum.Role, 200))
            : BatchMutation.Save(new EntityKey(EntityTypeEnum.Role, 200), cycle);
        return changes;
    }

    public static ValueSpec?[] Expected(int through)
    {
        var oracle = BatchFixture.Oracle(BatchFixture.Initial());
        for (var cycle = 1; cycle <= through; cycle++)
        {
            BatchFixture.Apply(oracle, Changes(cycle));
        }
        return oracle;
    }

    public static ReadSummary?[] Capture(BatchEngine engine) => Enumerable.Range(0, ArenaModel.KeyLimit)
        .Select(slot => engine.Read(EntityKey.FromSlot(slot))).ToArray();

    public static void VerifyCaptured(BatchEngine engine, ReadSummary?[] expected)
    {
        Check.That(Capture(engine).SequenceEqual(expected), "Recovered data/revisions differ from published state.");
    }

    public static void VerifyRecoveryState(RecoveryState state, int through)
    {
        var expected = Expected(through);
        var actual = new ValueSpec?[ArenaModel.KeyLimit];
        BatchFixture.Apply(actual, state.Roots.Select(root => root.Mutation).ToArray());
        var position = state.Position;
        foreach (var batch in state.Tail)
        {
            Check.That(batch.Receipt.Position == ++position, "Recovery snapshot has a missing transaction.");
            BatchFixture.Apply(actual, BatchCodec.Decode(batch.Payload));
        }
        Check.That(position == through && actual.SequenceEqual(expected), "Base/tail snapshot is inconsistent.");
    }
}
