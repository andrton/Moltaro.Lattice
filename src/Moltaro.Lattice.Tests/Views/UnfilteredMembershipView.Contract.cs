namespace Moltaro.Lattice.Tests.Views;

// Compile-only test fixture until the analyzer supplies this partial declaration.
internal readonly ref partial struct UnfilteredMembershipView : ILatticeEntityView<UnfilteredMembershipView, MembershipItem>
{
    static ReadOnlySpan<LatticeViewField> ILatticeEntityView<UnfilteredMembershipView, MembershipItem>.Fields => throw new NotSupportedException();

    static UnfilteredMembershipView ILatticeEntityView<UnfilteredMembershipView, MembershipItem>.Create(
        LatticeRecordView record, ReadOnlySpan<LatticeField> fields) => throw new NotSupportedException();
}
