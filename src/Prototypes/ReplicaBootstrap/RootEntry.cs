namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

// Protocol-model metadata; not the production managed-array control-entry layout.
internal sealed class RootEntry(RootKey key, long revision, NativeBlock? block)
{
    public RootKey Key { get; } = key;
    public long Revision { get; set; } = revision;
    public NativeBlock? Block { get; set; } = block;
}
