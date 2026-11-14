namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal struct RootEntry
{
    public int Key;
    public int Generation;
    public int Claims;
    public bool Live;
    public BlockAddress Address;
}
