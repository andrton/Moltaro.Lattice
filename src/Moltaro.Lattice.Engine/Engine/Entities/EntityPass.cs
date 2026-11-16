namespace Moltaro.Lattice.Engine.Entities;

// One enumeration of the entities of one entity type, as it stands between two of its steps: where it
// is in the primary index and the keys it has returned (ReturnedKeys), which EntityTable.TryReadNext
// reads and moves on. It is managed memory of the enumeration alone: the engine holds no reference to
// it and it holds nothing of the engine, no table of the index, no root and no slot of the protection
// of reads, so an enumeration that is abandoned without being disposed leaves nothing behind and keeps
// nothing from being reused (brief of S08, "Nothing is held between two steps").
//
// Where it is: the position of the next word of a table of the index to look at, and the version of
// that table (PrimaryIndex.TableAndVersion), which a growth changes. A position belongs to the table
// of its version, and a step that finds another table walks it from its start (EntityTable).
internal sealed class EntityPass
{
    // Where the step that staged its entity leaves the pass once that entity is built (Commit).
    private long _stagedPosition;

    private int _stagedVersion;

    public EntityPass(ReturnedKeys returned)
    {
        Returned = returned;
    }

    public ReturnedKeys Returned { get; }

    // The next word of the table of Version to look at. A new pass stands at the start of the first
    // table of the index, of version 0; if the index has grown since, its first step finds another
    // version and starts from the start all the same.
    public long Position { get; private set; }

    public int Version { get; private set; }

    // A step found its entity at the word before the position, in the table of the version, and staged
    // its key.
    public void Stage(long position, int version)
    {
        _stagedPosition = position;
        _stagedVersion = version;
    }

    // The entity the last step staged was built and is returned: the pass moves past it and remembers
    // its key.
    public void Commit()
    {
        Position = _stagedPosition;
        Version = _stagedVersion;
        Returned.Commit();
    }

    // A step found no more: the pass stands at the end of the table of the version.
    public void End(long position, int version)
    {
        Position = position;
        Version = version;
    }
}
