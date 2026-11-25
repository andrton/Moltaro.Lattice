using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Views;

// Compile-only test fixture until the analyzer supplies this partial declaration.
internal readonly ref partial struct StampView : ILatticeEntityView<StampView, StampSchema.Stamp>
{
    public long Number => throw new NotSupportedException();

    public LatticeValueView Label => throw new NotSupportedException();

    public LatticeValueView Text => throw new NotSupportedException();

    public LatticeValueView Data => throw new NotSupportedException();

    static ReadOnlySpan<LatticeViewField> ILatticeEntityView<StampView, StampSchema.Stamp>.Fields => throw new NotSupportedException();

    static StampView ILatticeEntityView<StampView, StampSchema.Stamp>.Create(LatticeRecordView record, ReadOnlySpan<LatticeField> fields) =>
        throw new NotSupportedException();
}
