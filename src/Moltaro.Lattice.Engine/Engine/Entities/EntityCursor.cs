namespace Moltaro.Lattice.Engine.Entities;

// Where an enumeration of the entities of one entity type stands: the next node of the primary
// index to look at, and the version of the index the enumeration started on. It holds nothing of the
// read gate between two items (brief of S06, "Enumeration until S08").
internal struct EntityCursor
{
    // The next node of the index to look at.
    public int Position;

    // The version of the index at the first item, while Started.
    public int Version;

    public bool Started;
}
