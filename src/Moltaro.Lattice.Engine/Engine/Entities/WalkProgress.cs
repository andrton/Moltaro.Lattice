namespace Moltaro.Lattice.Engine.Entities;

// What a search through an index remembers between two steps once it returns more than the entities of
// one step: the primary keys of the entities it has returned or passed over by Offset (ReturnedKeys, the
// set an enumeration has), so that it returns none twice, also one deleted and created again while it
// runs or one whose key moves ahead of it in the index (docs/decisions/queries.md, Q07.3.5); and the
// entry it stands after, in detached form, which owns its text (Storage.Ordered.IndexLayout.Detach), with
// the room where a step detaches the entry it is about to return. A search that finds one entity or none
// never makes one: it is made at the first step that returns an entity without seeing the end of the
// search (IndexSearch).
//
// Managed memory of the search alone: the engine holds no reference to it, and it holds nothing of the
// engine, so a search that is abandoned leaves nothing behind. What it holds counts against the memory
// limit of a read (ReadLimits): the bytes of its returned keys by their rule, and the bytes of its two
// entries. Room is made outside the protection of reads, as for the returned keys: a step asks whether it
// has room, and when it has none it leaves the protection, the room is counted and made, and the step
// reads its entry anew.
//
// Staged, then committed: a step detaches the entry of the entity it returns into the room for it and
// stages its key, and the search commits both once the entity is built (Commit), so that an entity whose
// build fails is read again by the next step.
internal sealed class WalkProgress
{
    // The progress of a walk of the entity type whose primary keys are given, that has returned nothing.
    public WalkProgress(in PrimaryKeys keys) =>
        throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // The primary keys the walk has returned or passed over.
    public ReturnedKeys Returned => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // The entry the walk stands after, in detached form; empty before it has committed one.
    public ReadOnlySpan<byte> Last => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // The bytes it holds, by the rule of the class.
    public long Bytes => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // Whether an entry of the length given can be detached without allocating. Inside the protection of
    // reads.
    public bool HasRoomFor(int length) => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // The most bytes it holds at once while it makes room for an entry of the length given.
    public long BytesWithRoomFor(int length) => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // Makes room for an entry of the length given. Outside the protection of reads.
    public void MakeRoom(int length) => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // The room where a step detaches the entry it is about to return, of the length given, which it has
    // room for.
    public Span<byte> StageEntry(int length) => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // The entity the last step staged was built and is returned: the walk stands after its entry and
    // remembers its key.
    public void Commit() => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // Lets go of everything it holds: for a search that failed and returns nothing more.
    public void Clear() => throw new NotSupportedException("The walk of ranges is not implemented yet.");
}
