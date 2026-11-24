using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// Places a collection's entries from managed values or the checked wire body. The caller
// installs and owns the derived lookup, if any, and checks uniqueness after filling it.
internal interface ICollectionEntrySource
{
    void Place(ref ValueHolder holder, in StoredField place, StructureLayout element, int offset, int count, bool allowInline);
}
