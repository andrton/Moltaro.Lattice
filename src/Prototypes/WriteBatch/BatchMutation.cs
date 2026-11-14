using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal readonly record struct BatchMutation(EntityKey Identity, MutationKindEnum Kind, ValueSpec Value)
{
    public static BatchMutation Save(EntityKey identity, int cycle) => new(identity, MutationKindEnum.Upsert,
        ValueSpec.ForKey(identity.Slot, cycle) with { TextGroup = (identity.Key + cycle * 7) % TextTable.Count });

    public static BatchMutation Delete(EntityKey identity) => new(identity, MutationKindEnum.Delete, default);
}
