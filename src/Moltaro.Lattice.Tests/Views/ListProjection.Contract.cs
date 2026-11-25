namespace Moltaro.Lattice.Tests.Views;

// Compile-only test fixture until the analyzer supplies this partial declaration.
internal readonly ref partial struct ListProjection : ILatticeEntityView<ListProjection, ByteProjectionModel>
{
    public LatticeListView Data => throw new NotSupportedException();

    static ReadOnlySpan<LatticeViewField> ILatticeEntityView<ListProjection, ByteProjectionModel>.Fields => throw new NotSupportedException();

    static ListProjection ILatticeEntityView<ListProjection, ByteProjectionModel>.Create(LatticeRecordView record, ReadOnlySpan<LatticeField> fields) =>
        throw new NotSupportedException();
}
