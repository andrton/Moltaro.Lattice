namespace Moltaro.Lattice.Storage.Text;

// The hash links and ownership count are used only under Writes. The high
// bit of Next names the bucket generation. Links are dense ordinals plus one.
internal struct TextSlot
{
    public long References;
    public uint Hash;
    public uint Length;
    public uint Id;
    public uint Previous;
    public uint Next;
    public bool WasInOldBucket;
}
