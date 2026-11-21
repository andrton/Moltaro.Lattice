using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Sets;

// The copy of one root as a record, for one read: a new record whose values the read puts into it
// in place, each in the form LatticeValueDeclaration.ValueType gives, a scalar boxed as a record
// holds it. Every value is made for this record alone, so the record owns what it holds.
//
// The record is the copy itself, made and filled under the protection of the root (IRootCopy.Copy),
// and Build only returns it: no code of the application runs there. LatticeRecord is a sealed class of
// the contract whose constructor and values array belong to the core, the values are boxed scalars,
// strings and arrays the engine makes, and nothing of a class of the application is reached.
internal struct RecordCopy : IRootCopy<LatticeRecord>
{
    private readonly LatticeEntityType _entityType;

    private LatticeRecord? _record;

    public RecordCopy(LatticeEntityType entityType)
    {
        _entityType = entityType;
    }

    public void Copy(in RootReader reader)
    {
        _record = new LatticeRecord(_entityType);
        RootValues.Read(reader, _entityType, _record.Values);
    }

    public readonly LatticeRecord Build() => _record!;

    // A record rents nothing.
    public readonly void Return()
    {
    }
}
