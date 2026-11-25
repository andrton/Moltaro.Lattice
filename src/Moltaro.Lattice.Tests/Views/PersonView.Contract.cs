using Moltaro.Lattice.Tests.NestedValues;

namespace Moltaro.Lattice.Tests.Views;

// Compile-only test fixture until the analyzer supplies this partial declaration.
internal readonly ref partial struct PersonView : ILatticeEntityView<PersonView, Person>
{
    public LatticeValueView Code => throw new NotSupportedException();

    public LatticeValueView Name => throw new NotSupportedException();

    public LatticeOptionalView<AddressView> Home => throw new NotSupportedException();

    public AddressView Work => throw new NotSupportedException();

    public LatticeListView Scores => throw new NotSupportedException();

    public LatticeListView Marks => throw new NotSupportedException();

    public LatticeListView Places => throw new NotSupportedException();

    public LatticeListView Pins => throw new NotSupportedException();

    public LatticeListView Grid => throw new NotSupportedException();

    public LatticeValueView Manager => throw new NotSupportedException();

    public LatticeValueView Photo => throw new NotSupportedException();

    public LatticeListView Notes => throw new NotSupportedException();

    public LatticeListView Files => throw new NotSupportedException();

    static ReadOnlySpan<LatticeViewField> ILatticeEntityView<PersonView, Person>.Fields => throw new NotSupportedException();

    static PersonView ILatticeEntityView<PersonView, Person>.Create(LatticeRecordView record, ReadOnlySpan<LatticeField> fields) =>
        throw new NotSupportedException();

    public readonly ref struct AddressView
    {
        public LatticeValueView City => throw new NotSupportedException();

        public int Zip => throw new NotSupportedException();

        public LatticeListView Lines => throw new NotSupportedException();

        public LatticeOptionalView<PointView> Geo => throw new NotSupportedException();
    }

    public readonly ref struct PointView
    {
        public double Lat => throw new NotSupportedException();

        public double Lon => throw new NotSupportedException();
    }
}
