namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal struct TextEntry
{
    public int Generation;
    public int Stored;
    public int Prepared;
    public int Indexed;
    public BlockAddress Address;
    public bool Exists => Stored + Prepared + Indexed > 0;
}
