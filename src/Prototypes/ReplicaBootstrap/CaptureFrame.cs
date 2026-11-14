namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal sealed record CaptureFrame(int Slot, RootKey? Key, long Revision, byte[]? Block);
