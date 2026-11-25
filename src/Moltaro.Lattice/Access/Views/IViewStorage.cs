using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Views;

// The boundary between schema access and the engine's resident storage. The
// caller supplies the read epoch. Borrowed outputs carry the scope of the
// cursor supplied by value. The other methods take a scoped input.
internal interface IViewStorage
{
    int ChildPosition(scoped in ViewRecord row, int owned, LatticeField field);

    bool IsNull(scoped in ViewRecord row, int position);

    TValue ReadScalar<TValue>(scoped in ViewRecord row, int position);

    string ReadString(scoped in ViewRecord row, int position);

    (int Length, int Parts) ByteShape(scoped in ViewRecord row, int position);

    ReadOnlySpan<byte> GetPart(ViewRecord row, int position, int part);

    ViewCollection Collection(ViewRecord row, int position);

    ViewRecord Entry(ViewCollection collection, int ordinal);

    ViewPackedBlock ReadPackedListBlock(ViewCollection collection, int block);

    int FindEntry(scoped in ViewCollection collection, in IndexValue key);
}
