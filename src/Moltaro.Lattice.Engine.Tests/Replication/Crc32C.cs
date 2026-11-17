using System.Numerics;

namespace Moltaro.Lattice.Engine.Tests.Replication;

// The CRC32C of the tests, the check of a frame by the planner's decision of 17 November 2026 after the
// study of S10, written apart from the engine so that the tests can make a frame with a valid check and
// state the check of the engine. It is CRC-32C (Castagnoli) as iSCSI defines it: the reflected
// polynomial 0x82F63B78, which BitOperations.Crc32C computes, with an initial value and a final
// complement of all ones. Its check value, of the nine bytes of "123456789", is 0xE3069283.
internal static class Crc32C
{
    public static uint Of(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;

        foreach (var value in bytes)
        {
            crc = BitOperations.Crc32C(crc, value);
        }

        return ~crc;
    }
}
