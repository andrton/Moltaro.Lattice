using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples.Basics;

// Lets the planned sample compile until the analyzer provides this partial declaration.
internal readonly ref partial struct UserView : ILatticeEntityView<UserView, User>
{
    public bool Enabled => throw new NotSupportedException();

    static ReadOnlySpan<LatticeViewField> ILatticeEntityView<UserView, User>.Fields => throw new NotSupportedException();

    static UserView ILatticeEntityView<UserView, User>.Create(LatticeRecordView record, ReadOnlySpan<LatticeField> fields) =>
        throw new NotSupportedException();
}
