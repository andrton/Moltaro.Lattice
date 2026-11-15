namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// The size classes of blocks as the milestone contract gives them ("Native layout and
// allocation"), header included: 16-byte steps through 1 KiB, 256-byte steps through 4 KiB, then
// powers of two through 64 KiB. The smallest class is the 32-byte header alone, and the largest
// holds 65,504 bytes of payload, the chunk payload of the contract. They are written out here from
// the specification, so that the tests compare the arena with the specification and not with
// itself.
internal static class SpecifiedSizeClasses
{
    public const int HeaderSize = 32;

    public const int LargestPayload = 65_504;

    private static readonly List<int> Sizes = Build();

    // Every class, the smallest first.
    public static IReadOnlyList<int> All => Sizes;

    // The smallest class that holds the header and this many bytes of payload.
    public static int For(int payloadLength) => Sizes.First(size => size >= HeaderSize + payloadLength);

    // The payload lengths whose blocks take this class: from one byte more than the next smaller
    // class holds, or from 0 for the smallest class, up to the class without its header.
    public static (int Smallest, int Largest) PayloadLengthsOf(int sizeClass)
    {
        var index = Sizes.IndexOf(sizeClass);
        var smallest = index == 0 ? 0 : Sizes[index - 1] - HeaderSize + 1;

        return (smallest, sizeClass - HeaderSize);
    }

    private static List<int> Build()
    {
        var sizes = new List<int>();

        for (var size = HeaderSize; size <= 1024; size += 16)
        {
            sizes.Add(size);
        }

        for (var size = 1024 + 256; size <= 4096; size += 256)
        {
            sizes.Add(size);
        }

        for (var size = 8 * 1024; size <= 64 * 1024; size *= 2)
        {
            sizes.Add(size);
        }

        return sizes;
    }
}
