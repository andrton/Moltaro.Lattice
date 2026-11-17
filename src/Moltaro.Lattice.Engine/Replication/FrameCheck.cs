using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace Moltaro.Lattice.Replication;

// The check at the end of every frame: CRC-32C (Castagnoli), as iSCSI defines it — the reflected
// polynomial 0x82F63B78, an initial value and a final complement of all ones — over the header and the
// payload (the planner's decision of 17 November 2026 after the study of S10, point 1). It guards
// against accidental damage and framing faults only: the engine authenticates nobody, and a digest
// without a key would add nothing. BitOperations.Crc32C computes the polynomial with the instruction of
// the processor where it has one, eight bytes at a time.
internal static class FrameCheck
{
    // The check of the bytes.
    public static uint Of(ReadOnlySpan<byte> bytes) => ~Update(uint.MaxValue, bytes);

    // The check of the bytes of a sequence, whose segments are taken in their order.
    public static uint Of(in ReadOnlySequence<byte> bytes)
    {
        var crc = uint.MaxValue;

        foreach (var segment in bytes)
        {
            crc = Update(crc, segment.Span);
        }

        return ~crc;
    }

    // The register after the bytes, before the final complement. A word is read little-endian, which
    // is the order in which the reflected polynomial takes the bytes, so the result is the same on
    // every machine.
    private static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    {
        while (bytes.Length >= sizeof(ulong))
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
            bytes = bytes[sizeof(ulong)..];
        }

        foreach (var value in bytes)
        {
            crc = BitOperations.Crc32C(crc, value);
        }

        return crc;
    }
}
