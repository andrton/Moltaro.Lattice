using Moltaro.Lattice.Tests.NestedValues.Collections;

namespace Moltaro.Lattice.Tests.Views;

// Compile-only test fixture until the analyzer supplies this partial declaration.
internal readonly ref partial struct CollectionItemView : ILatticeEntityView<CollectionItemView, CollectionItem>
{
    public LatticeOptionalView<LatticeMapView> Options => throw new NotSupportedException();

    public LatticeOptionalView<LatticeSetView> Rights => throw new NotSupportedException();

    public LatticeMapView Numbers => throw new NotSupportedException();

    public LatticeSetView Bytes => throw new NotSupportedException();

    public LatticeSetView Scores => throw new NotSupportedException();

    static ReadOnlySpan<LatticeViewField> ILatticeEntityView<CollectionItemView, CollectionItem>.Fields => throw new NotSupportedException();

    static CollectionItemView ILatticeEntityView<CollectionItemView, CollectionItem>.Create(LatticeRecordView record, ReadOnlySpan<LatticeField> fields) =>
        throw new NotSupportedException();
}
