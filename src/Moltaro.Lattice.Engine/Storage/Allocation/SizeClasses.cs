using System.Numerics;

namespace Moltaro.Lattice.Storage.Allocation;

// The size classes of blocks, header included (milestone contract, "Native layout and
// allocation"): 16-byte steps from 32 bytes through 1 KiB, 256-byte steps through 4 KiB, then
// powers of two through Arena.MaxBlockSize. A class is named by its index, the smallest first, and
// the arena keeps that index in one byte of a region's metadata. Every class is a multiple of 16
// bytes, so every slot of a region starts on a 16-byte boundary. The classes are computed, not
// kept in a table.
internal static class SizeClasses
{
    // 63 classes in 16-byte steps (32 to 1,024 bytes), 12 in 256-byte steps (1,280 to 4,096 bytes)
    // and 4 powers of two (8 KiB to 64 KiB).
    public const int Count = 79;

    // The smallest class: a header and no payload.
    public const int Smallest = BlockHeader.Size;

    // The index of the class of 1,024 bytes, the last of the 16-byte steps.
    private const int LastOfSixteenByteSteps = 62;

    // The index of the class of 4,096 bytes, the last of the 256-byte steps.
    private const int LastOfQuarterKibSteps = 74;

    // The index of the smallest class that holds the header and payloadLength bytes. A negative
    // length, or one that does not fit Arena.MaxBlockSize with the header, is refused with
    // ArgumentOutOfRangeException.
    public static int IndexFor(int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadLength, Arena.MaxBlockSize - BlockHeader.Size);

        var blockSize = BlockHeader.Size + payloadLength;

        if (blockSize <= 1024)
        {
            return (blockSize - Smallest + 15) / 16;
        }

        if (blockSize <= 4096)
        {
            return LastOfSixteenByteSteps + (blockSize - 1024 + 255) / 256;
        }

        // 4,097 to 8,192 bytes take 8 KiB, and each further doubling one class more.
        return LastOfQuarterKibSteps + 1 + BitOperations.Log2((uint)blockSize - 1) - 12;
    }

    // The bytes of a block of the class with this index, header included.
    public static int SizeOf(int index)
    {
        if (index <= LastOfSixteenByteSteps)
        {
            return Smallest + 16 * index;
        }

        if (index <= LastOfQuarterKibSteps)
        {
            return 1024 + 256 * (index - LastOfSixteenByteSteps);
        }

        return 4096 << (index - LastOfQuarterKibSteps);
    }
}
