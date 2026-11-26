namespace Moltaro.Lattice.Storage.Text;

// Before unlink the cursor holds an ID. After unlink it holds the native
// placement. The ID is quarantined until all of that placement is released.
internal struct TextReclamation(uint id)
{
    public uint Id = id;
    public ArenaAddress Address;
    public ArenaAddress PendingChunk;
    public int ChunkCount;
    public int NextChunk;
    public bool Detached;
}
