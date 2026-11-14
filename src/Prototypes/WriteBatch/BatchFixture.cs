using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal static class BatchFixture
{
    public static BatchMutation[] Initial() => Enumerable.Range(0, 256)
        .Select(index => BatchMutation.Save(new EntityKey((EntityTypeEnum)(index % 2), index / 2), 0)).ToArray();

    public static BatchMutation[] Changes(int count, int cycle) => Enumerable.Range(0, count)
        .Select(index => BatchMutation.Save(new EntityKey((EntityTypeEnum)(index % 2), index / 2), cycle)).ToArray();

    public static ValueSpec?[] Oracle(BatchMutation[] initial)
    {
        var result = new ValueSpec?[ArenaModel.KeyLimit];
        Apply(result, initial);
        return result;
    }

    public static void Apply(ValueSpec?[] oracle, BatchMutation[] changes)
    {
        foreach (var change in changes)
        {
            oracle[change.Identity.Slot] = change.Kind == MutationKindEnum.Delete ? null : change.Value;
        }
    }

    public static void ExpectFailure(Action action, string expectedMessage)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException exception) when (exception.Message == expectedMessage)
        {
            return;
        }

        throw new InvalidOperationException("Expected rejection was not observed: " + expectedMessage);
    }
}
