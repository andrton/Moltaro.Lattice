namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

internal sealed class OwnedCollectionItem<TKey, TCollection>
    where TKey : notnull
    where TCollection : class
{
    public TKey Id { get; set; } = default!;

    public int Case { get; set; }

    public int Stamp { get; set; }

    public TCollection? Values { get; set; }
}
