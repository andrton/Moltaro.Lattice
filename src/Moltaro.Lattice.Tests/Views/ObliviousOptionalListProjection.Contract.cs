namespace Moltaro.Lattice.Tests.Views;

// Compile-only test fixture until the analyzer supplies this partial declaration.
internal readonly ref partial struct ObliviousOptionalListProjection : ILatticeEntityView<ObliviousOptionalListProjection, ObliviousProjectionModel>
{
    public LatticeOptionalView<LatticeListView> Numbers => throw new NotSupportedException();

    static ReadOnlySpan<LatticeViewField> ILatticeEntityView<ObliviousOptionalListProjection, ObliviousProjectionModel>.Fields => throw new NotSupportedException();

    static ObliviousOptionalListProjection ILatticeEntityView<ObliviousOptionalListProjection, ObliviousProjectionModel>.Create(LatticeRecordView record, ReadOnlySpan<LatticeField> fields) =>
        throw new NotSupportedException();
}
