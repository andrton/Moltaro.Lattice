using System.Collections.Concurrent;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Forwarding;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Sets;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine;

// The parts of an open database (EngineDatabase), made together by one method when it opens (Build) and
// published as one field, which the close clears once it has freed the arena: a part added here is let go
// of with the others, so an instance kept alive after its close holds none of them, nor the managed memory
// of its root entries, its indexes and its text table. Immutable once made: a member reads the field once
// and finds every part, or none, and then reports ObjectDisposedException. The network of a master and of a
// child is not among them: it is made at moments of its own during the open, and kept beside them.
internal sealed class DatabaseParts
{
    public required LatticeSchema Schema { get; init; }

    public required Arena Arena { get; init; }

    public required RootTable Roots { get; init; }

    public required RetiredMemory Retired { get; init; }

    public required PositionClock Clock { get; init; }

    // The lock of the writes, which the counters and the drain take as well (Writes.WriteCoordinator).
    public required Lock Writes { get; init; }

    public required WriteCoordinator Coordinator { get; init; }

    // The tail of the transactions a master holds for its children; on a child it never retains.
    public required DeliveryTail Delivery { get; init; }

    // On a child: where its writes go, to its master; null on a master.
    public required Forwarder? Forwarder { get; init; }

    public required EntityTables Tables { get; init; }

    public required Dictionary<LatticeEntityType, SetOperations> Operations { get; init; }

    public required Dictionary<LatticeEntityType, RecordSet> RecordSets { get; init; }

    // The sets of classes, made once for each class and shared.
    public ConcurrentDictionary<Type, object> ClassSets { get; } = new();
}
