namespace Moltaro.Lattice.Tests.Views;

// Compile-only test fixture until the analyzer supplies this partial declaration.
internal readonly ref partial struct ObliviousRequiredListProjection : ILatticeEntityView<ObliviousRequiredListProjection, ObliviousProjectionModel>
{
    public LatticeListView Numbers => throw new NotSupportedException();

    static ReadOnlySpan<LatticeViewField> ILatticeEntityView<ObliviousRequiredListProjection, ObliviousProjectionModel>.Fields => throw new NotSupportedException();

    static ObliviousRequiredListProjection ILatticeEntityView<ObliviousRequiredListProjection, ObliviousProjectionModel>.Create(LatticeRecordView record, ReadOnlySpan<LatticeField> fields) =>
        throw new NotSupportedException();
}
