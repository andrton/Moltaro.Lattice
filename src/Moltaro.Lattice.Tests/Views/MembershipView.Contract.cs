namespace Moltaro.Lattice.Tests.Views;

// Compile-only test fixture until the analyzer supplies this partial declaration.
internal readonly ref partial struct MembershipView : ILatticeEntityView<MembershipView, MembershipItem>
{
    public int Id => throw new NotSupportedException();

    public AddressView Home => throw new NotSupportedException();

    static ReadOnlySpan<LatticeViewField> ILatticeEntityView<MembershipView, MembershipItem>.Fields => throw new NotSupportedException();

    static MembershipView ILatticeEntityView<MembershipView, MembershipItem>.Create(LatticeRecordView record, ReadOnlySpan<LatticeField> fields) =>
        throw new NotSupportedException();

    public readonly ref struct AddressView
    {
        public int Zip => throw new NotSupportedException();
    }
}
