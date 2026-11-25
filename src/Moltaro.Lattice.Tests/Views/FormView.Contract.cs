using Moltaro.Lattice.Tests.NestedValues.Copies;

namespace Moltaro.Lattice.Tests.Views;

// Compile-only test fixture until the analyzer supplies this partial declaration.
internal readonly ref partial struct FormView : ILatticeEntityView<FormView, Form>
{
    public LabelView Main => throw new NotSupportedException();

    public LatticeOptionalView<LabelView> Spare => throw new NotSupportedException();

    public LatticeListView MaybeGrades => throw new NotSupportedException();

    static ReadOnlySpan<LatticeViewField> ILatticeEntityView<FormView, Form>.Fields => throw new NotSupportedException();

    static FormView ILatticeEntityView<FormView, Form>.Create(LatticeRecordView record, ReadOnlySpan<LatticeField> fields) =>
        throw new NotSupportedException();

    public readonly ref struct LabelView
    {
        public Grade? Grade => throw new NotSupportedException();

        public LatticeValueView Data => throw new NotSupportedException();
    }
}
