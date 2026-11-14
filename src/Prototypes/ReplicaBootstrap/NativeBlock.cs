using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal sealed unsafe class NativeBlock : IDisposable
{
    private byte* _pointer;
    private static int _liveBlocks;
    private static int _peakBlocks;

    public NativeBlock(byte[] bytes)
    {
        Check.That(bytes.Length == BlockCodec.Bytes, "Incorrect native block size.");
        _pointer = (byte*)NativeMemory.Alloc(BlockCodec.Bytes);
        if (_pointer is null)
        {
            throw new OutOfMemoryException();
        }
        bytes.CopyTo(new Span<byte>(_pointer, BlockCodec.Bytes));
        var live = Interlocked.Increment(ref _liveBlocks);
        int previous;
        do
        {
            previous = Volatile.Read(ref _peakBlocks);
            if (previous >= live) { break; }
        }
        while (Interlocked.CompareExchange(ref _peakBlocks, live, previous) != previous);
    }

    public static int LiveBlocks => Volatile.Read(ref _liveBlocks);
    public static int PeakBlocks => Volatile.Read(ref _peakBlocks);

    public byte[] Copy()
    {
        ObjectDisposedException.ThrowIf(_pointer is null, this);
        return new ReadOnlySpan<byte>(_pointer, BlockCodec.Bytes).ToArray();
    }

    public void Dispose()
    {
        if (_pointer is null) { return; }
        NativeMemory.Free(_pointer);
        _pointer = null;
        Interlocked.Decrement(ref _liveBlocks);
    }
}
