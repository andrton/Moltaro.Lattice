namespace Moltaro.Lattice.Tests.Views;

// Compile-only test fixture until the analyzer supplies this partial declaration.
internal readonly ref partial struct BinaryProjection : ILatticeEntityView<BinaryProjection, ByteProjectionModel>
{
    public LatticeValueView Data => throw new NotSupportedException();

    public int? Level => throw new NotSupportedException();

    public LatticeOptionalView<LatticeListView> Numbers => throw new NotSupportedException();

    static ReadOnlySpan<LatticeViewField> ILatticeEntityView<BinaryProjection, ByteProjectionModel>.Fields => throw new NotSupportedException();

    static BinaryProjection ILatticeEntityView<BinaryProjection, ByteProjectionModel>.Create(LatticeRecordView record, ReadOnlySpan<LatticeField> fields) =>
        throw new NotSupportedException();
}
