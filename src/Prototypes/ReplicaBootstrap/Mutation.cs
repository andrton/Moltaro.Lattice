namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal sealed record Mutation(MutationKindEnum Kind, RootKey Key, int Number, int[] Items)
{
    public static Mutation Put(RootKey key, int number, params int[] items) =>
        new(MutationKindEnum.Upsert, key, number, items);

    public static Mutation Set(RootKey key, int number) =>
        new(MutationKindEnum.SetNumber, key, number, []);

    public static Mutation Add(RootKey key, int item) =>
        new(MutationKindEnum.Append, key, item, []);

    public static Mutation Remove(RootKey key) => new(MutationKindEnum.Delete, key, 0, []);
}
