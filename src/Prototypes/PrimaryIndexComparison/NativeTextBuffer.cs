using System.Runtime.InteropServices;
using System.Text;

namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

// Fixture owner, not the production arena. Every key view dies before its owner.
internal sealed unsafe class NativeTextBuffer : IDisposable
{
    internal static readonly UTF8Encoding Encoding = new(false, true);
    private nint _address;
    private readonly int _length;

    public NativeTextBuffer(string text)
    {
        _length = Encoding.GetByteCount(text);
        if (_length > NativeTextKey.MaximumProbeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(text));
        }

        _address = (nint)NativeMemory.Alloc((nuint)Math.Max(_length, 1));
        if (_address == 0)
        {
            throw new OutOfMemoryException();
        }

        Encoding.GetBytes(text, new Span<byte>((void*)_address, _length));
    }

    public NativeTextKey Key(StringComparison comparison) => new(_address, _length, comparison);

    public void Dispose()
    {
        NativeMemory.Free((void*)_address);
        _address = 0;
    }
}
