using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// A case of a scenario for one fixed key type, which FixedBuild.Dispatch picks by name.
internal interface IFixedCase
{
    void Run<TEntry, TKey>()
        where TEntry : unmanaged, IFixedEntry<TEntry, TKey>
        where TKey : IComparable<TKey>;
}
