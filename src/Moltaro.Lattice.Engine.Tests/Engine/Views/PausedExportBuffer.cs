using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Engine.Tests.Engine.Views;

// Pauses BlockExport when it requests the next buffer span after writing the chosen bytes.
// The default pauses after the layout version, before the exporter reads the root descriptors.
internal sealed class PausedExportBuffer : IBufferWriter<byte>
{
    private readonly byte[] _storage;
    private readonly int _start;
    private readonly int _capacity;
    private readonly ViewerPause _pause;
    private readonly int _pauseAfter;
    private int _written;

    public PausedExportBuffer(int capacity, Span<byte> nativePayload, ViewerPause pause,
        int pauseAfter = sizeof(uint))
    {
        _capacity = capacity;
        _pause = pause;
        _pauseAfter = pauseAfter;
        _storage = GC.AllocateUninitializedArray<byte>(capacity + 8, pinned: true);

        // Make the destination payload skew three bytes relative to the native
        // payload, regardless of where the runtime placed the pinned output array.
        var skew = (int)(Unsafe.ByteOffset(ref MemoryMarshal.GetReference(nativePayload), ref _storage[0]) & 7);
        _start = (3 - sizeof(uint) - skew) & 7;
        var firstPayloadByte = _start + sizeof(uint);
        Assert.Equal(3, (int)(Unsafe.ByteOffset(ref MemoryMarshal.GetReference(nativePayload), ref _storage[firstPayloadByte]) & 7));
    }

    public ReadOnlyMemory<byte> Written => _storage.AsMemory(_start, _written);

    public void Advance(int count)
    {
        Assert.InRange(count, 0, _capacity - _written);
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Assert.InRange(sizeHint, 0, _capacity - _written);
        if (_written == _pauseAfter)
        {
            _pause.Hold();
        }

        return _storage.AsMemory(_start + _written, _capacity - _written);
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
}
