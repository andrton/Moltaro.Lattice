using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed unsafe class NativeSegment : IDisposable
{
    public const int Bytes = 8192;
    private byte* _pointer;
    private static long _outstandingBytes;

    public NativeSegment(int id, int capacity)
    {
        Id = id;
        Capacity = capacity;
        States = new BlockStateEnum[Bytes / capacity];
        Owners = new BlockOwner[States.Length];
        Lengths = new int[States.Length];
        FreeSlots = new Stack<int>(States.Length);
        for (var slot = States.Length - 1; slot >= 0; slot--)
        {
            FreeSlots.Push(slot);
        }

        _pointer = (byte*)NativeMemory.AllocZeroed(Bytes);
        if (_pointer is null)
        {
            throw new OutOfMemoryException();
        }

        Interlocked.Add(ref _outstandingBytes, Bytes);
    }

    public static long OutstandingBytes => Interlocked.Read(ref _outstandingBytes);
    public int Id { get; }
    public int Capacity { get; }
    public BlockStateEnum[] States { get; }
    public BlockOwner[] Owners { get; }
    public int[] Lengths { get; }
    public Stack<int> FreeSlots { get; }
    public int CandidateCursor { get; set; }
    public int Used => States.Length - FreeSlots.Count;

    // The caller owns the relevant root/text gate, or a private prepared block.
    public Span<byte> Span(int offset, int length)
    {
        ObjectDisposedException.ThrowIf(_pointer is null, this);
        Check.That(offset >= 0 && offset + length <= Bytes, "Native segment bounds.");
        return new Span<byte>(_pointer + offset, length);
    }

    public void Dispose()
    {
        if (_pointer is null)
        {
            return;
        }

        NativeMemory.Free(_pointer);
        _pointer = null;
        Interlocked.Add(ref _outstandingBytes, -Bytes);
    }
}
