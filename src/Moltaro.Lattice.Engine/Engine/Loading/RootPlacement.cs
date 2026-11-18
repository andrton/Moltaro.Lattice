using System.Buffers;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;

namespace Moltaro.Lattice.Engine.Loading;

// Places the roots a child receives from its master in the child's arena and publishes them, and ends
// the load (docs/decisions/replication-and-hosting.md, "Initial capture and readiness handoff").
//
// A root is placed and published by the one rule the child applies what it receives by, under the child's
// lock of the writes and inside the barrier of the close, as a write is (ReceivedChanges.Place): a key
// arrives twice in a load when the master wrote while its capture walked its slots, and the later state
// stays. A root the arena refuses is placed by the rule of a write refused for memory (ReceivedChanges).
//
// What a root brings and what is checked of it, beyond what BlockImport checks: its type id names an entity
// type of the schema the engine stores, and its last mutation sequence is not 0. The key comes from the root
// as it is stored, and the child hashes it under its own seed: nothing hashed travels, so that nobody who
// does not know the seed of a node can choose keys that share a hash. The revision of a loaded entity is its
// revision on the master: the stamp of its entry is the sequence of its last mutation there. Each failure is
// a LatticeException with ValidationFailed, and nothing of the root is left.
//
// At the end of the load (End), the number of roots received has to be the number LoadEnd announces, and
// the readiness position R at least the last mutation of every root; then the capture start S becomes
// the sequence of the child, which applies the transactions after it up to R before it is Ready
// (Following.Follower).
internal sealed class RootPlacement
{
    private readonly ReceivedChanges _changes;

    private readonly EntityTables _tables;

    // The roots received, and the latest of their last mutations.
    private ulong _received;

    private ulong _latest;

    public RootPlacement(ReceivedChanges changes, EntityTables tables)
    {
        _changes = changes;
        _tables = tables;
    }

    // Places and publishes the root whose bytes are given: the bytes after its length, as
    // Replication.Load.RootAssembler hands it out (see the class).
    public void Place(ReadOnlySequence<byte> bytes)
    {
        var reader = new SequenceReader<byte>(bytes);

        if (!RootEnvelope.TryRead(ref reader, out var typeId, out var lastMutation))
        {
            throw Protocol.Invalid($"A root of {bytes.Length} bytes is shorter than its envelope.");
        }

        if (!_tables.TryGetStored(typeId, out var table) || table.Layout is not { } layout)
        {
            throw Protocol.Invalid($"A root names the entity type {typeId}, which the schema of the child does not store.");
        }

        if (lastMutation == 0)
        {
            throw Protocol.Invalid($"A root of {layout.EntityType.Name} has no last mutation.");
        }

        _changes.Place(table, lastMutation, reader, _received);

        _received++;
        _latest = Math.Max(_latest, lastMutation);
    }

    // Ends the load (see the class).
    public void End(LoadEndBody end)
    {
        if (_received != end.Roots)
        {
            throw Protocol.Invalid($"The child received {_received} roots, and the master announces {end.Roots} at the end of the load.");
        }

        if (_latest > end.Readiness)
        {
            throw Protocol.Invalid($"A root has the last mutation {_latest}, after the end position {end.Readiness} of the capture.");
        }

        _changes.EndLoad(end.CaptureStart);
    }
}
