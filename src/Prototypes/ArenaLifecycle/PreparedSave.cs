namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed class PreparedSave : IDisposable
{
    private readonly ArenaModel _arena;

    public PreparedSave(ArenaModel arena, RootClaim claim)
    {
        _arena = arena;
        Claim = claim;
    }

    public RootClaim Claim { get; }
    public BlockAddress? Root { get; set; }
    public List<BlockAddress> Chunks { get; } = new(RootCodec.MaximumChunks);
    public TextToken? Text { get; set; }
    public PreparedIndexEntry? Index { get; set; }
    public bool Completed { get; set; }

    public void Dispose()
    {
        if (!Completed)
        {
            _arena.Abort(this);
        }
    }
}
