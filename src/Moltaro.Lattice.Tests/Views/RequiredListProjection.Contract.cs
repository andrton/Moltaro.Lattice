namespace Moltaro.Lattice.Tests.Views;

// Compile-only test fixture until the analyzer supplies this partial declaration.
internal readonly ref partial struct RequiredListProjection : ILatticeEntityView<RequiredListProjection, ByteProjectionModel>
{
    public LatticeListView Numbers => throw new NotSupportedException();

    static ReadOnlySpan<LatticeViewField> ILatticeEntityView<RequiredListProjection, ByteProjectionModel>.Fields => throw new NotSupportedException();

    static RequiredListProjection ILatticeEntityView<RequiredListProjection, ByteProjectionModel>.Create(LatticeRecordView record, ReadOnlySpan<LatticeField> fields) =>
        throw new NotSupportedException();
}
