namespace Moltaro.Lattice.Replication.Forwarding;

// The reply's command count and its low-bit-first outcomes. A default value
// is the short success reply for a request that carried no update.
internal readonly record struct UpdateBitmap(int Count, byte[]? Bits);
