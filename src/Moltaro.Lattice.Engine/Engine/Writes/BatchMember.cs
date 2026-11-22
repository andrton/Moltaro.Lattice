using System.Buffers;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;

namespace Moltaro.Lattice.Engine.Writes;

// One operation of a write batch as the write path executes it (WriteCoordinator.SubmitBatchAsync): what it
// does, to which entity, under which condition, and for an insert, a replace or an upsert the root of the
// entity in the load format, after its envelope, which the write path places when it prepares the batch, as
// it places the root of a forwarded write (WriteExecutor). Root is empty for a delete. The members of a
// batch are in the order they were added.
internal readonly record struct BatchMember(
    WriteKindEnum Kind,
    EntityTable Table,
    EntityKey Key,
    LatticeRevision? ExpectedRevision,
    ReadOnlySequence<byte> Root)
{
    // The number of the member at the place given among the members of a batch, which its change stores in
    // the root entry with its stamp (Entities.PreparedChange.Member) and which is part of the revision
    // (Entities.EntityTable.RevisionOf): from 1, in the order of the members, for a batch of two or more,
    // so that a read can tell a member of a publication of several changes by a number other than 0; 0 for
    // the one member of a batch of one, whose revision is then the revision of a single write, as its
    // publication is one of a single change.
    public static uint NumberOf(int place, int count) => count == 1 ? 0 : (uint)place + 1;

    // The place among the members of a batch of the member whose number is given (NumberOf), which a refusal
    // reports as LatticeException.OperationIndex.
    public static int PlaceOf(uint number) => number == 0 ? 0 : (int)number - 1;
}
