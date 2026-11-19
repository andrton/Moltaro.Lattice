using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// A case of a scenario for one form of a text key, which TextForms.Dispatch picks by name.
internal interface ITextCase
{
    void Run<TEntry, TOrder>(int length)
        where TEntry : unmanaged, ITextForm<TEntry, TOrder>
        where TOrder : struct, IEntryOrder<TEntry, TextProbe>;
}
