using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// Form 2: the entry holds a copy of the key, its length (2 bytes) and the identity, packed: L + 10
// bytes. Every key of one measurement has the same length L, so this fixed slot is the best case of a
// layout of variable entries, which adds a slot of about four bytes per entry (an offset and a length,
// as LMDB's 2-byte index and 8-byte node header or LeanStore's 10-byte slot).
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct CopyEntry<TBytes> : ITextForm<CopyEntry<TBytes>, CopyOrder<TBytes>>
    where TBytes : unmanaged
{
    // Where the bytes of the key begin in the entry.
    public const int TextOffset = 2;

    private ushort _length;

    private TBytes _bytes;

    private ulong _id;

    public static string Name => $"Copy{Unsafe.SizeOf<TBytes>()}";

    public static bool SharesText => false;

    public readonly int Length => _length;

    public readonly ulong Id => _id;

    public readonly ReadOnlySpan<byte> Text => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<TBytes, byte>(ref Unsafe.AsRef(in _bytes)), _length);

    public static CopyEntry<TBytes> Make(ulong id, ReadOnlySpan<byte> text, ulong textAddress)
    {
        var entry = new CopyEntry<TBytes> { _length = (ushort)text.Length, _id = id };

        text.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<TBytes, byte>(ref entry._bytes), Unsafe.SizeOf<TBytes>()));

        return entry;
    }

    public static CopyOrder<TBytes> OrderFor(TextWorld world) => default;
}
