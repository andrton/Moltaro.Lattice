using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

internal static class CollectionProbes
{
    public static IndexValue Bind<TValue>(LatticeField field, TValue value)
    {
        var declaration = field.Declaration.Key ?? field.Declaration.Element!;

        return IndexValue.Of(value).Bind(declaration, field.Name, nameof(value));
    }
}
