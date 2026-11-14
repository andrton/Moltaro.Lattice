using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed unsafe class NativeImage : IDisposable
{
    private byte* _data;
    private readonly int _length;

    public NativeImage(int length)
    {
        _length = length;
        _data = (byte*)NativeMemory.Alloc((nuint)length);
    }

    public Span<byte> Slice(int offset, int length)
    {
        return new Span<byte>(_data, _length).Slice(offset, length);
    }

    public void Dispose()
    {
        NativeMemory.Free(_data);
        _data = null;
    }
}
