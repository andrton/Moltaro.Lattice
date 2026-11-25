namespace Moltaro.Lattice.Storage.Text;

// The last release of a text can yield while finding its bucket or returning its
// detached blocks. It keeps neither a slot reference nor the old bucket table.
internal struct TextReclamation(ArenaAddress address)
{
    public ArenaAddress Address = address;

    public ArenaAddress PendingChunk;

    public long Bucket;

    public int BucketVersion = -1;

    public uint Id;

    public int ChunkCount;

    public int NextChunk;

    public bool Detached;
}
