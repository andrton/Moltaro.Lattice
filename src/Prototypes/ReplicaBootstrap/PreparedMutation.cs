namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal sealed class PreparedMutation(RootEntry target, bool isNew, NativeBlock? block, ResolvedEffect effect)
    : IDisposable
{
    private NativeBlock? _block = block;

    public RootEntry Target { get; } = target;
    public bool IsNew { get; } = isNew;
    public ResolvedEffect Effect { get; } = effect;

    public NativeBlock? TakeBlock()
    {
        var result = _block;
        _block = null;
        return result;
    }

    public void Dispose() => _block?.Dispose();
}
