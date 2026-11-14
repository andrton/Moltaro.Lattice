using System.Buffers.Binary;

namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal static class BlockCodec
{
    public const int Bytes = 64;
    public const int MaximumItems = (Bytes - 8) / 4;

    public static byte[] Create(int number, int[] items)
    {
        if (items.Length > MaximumItems)
        {
            throw new ArgumentException("Prototype list is full.");
        }

        var block = new byte[Bytes];
        WriteInt(block, 0, number);
        WriteInt(block, 4, items.Length);
        for (var index = 0; index < items.Length; index++)
        {
            WriteInt(block, 8 + index * 4, items[index]);
        }
        return block;
    }

    public static DocumentValue Decode(ReadOnlySpan<byte> block)
    {
        Check.That(block.Length == Bytes, "Incorrect block length.");
        var count = ReadInt(block, 4);
        Check.That(count is >= 0 and <= MaximumItems, "Incorrect list length.");
        var items = new int[count];
        for (var index = 0; index < count; index++)
        {
            items[index] = ReadInt(block, 8 + index * 4);
        }
        return new DocumentValue(ReadInt(block, 0), items);
    }

    public static void SetNumber(Span<byte> block, int value) => WriteInt(block, 0, value);

    public static void Append(Span<byte> block, int value)
    {
        var count = ReadInt(block, 4);
        Check.That(count < MaximumItems, "Prototype list is full.");
        WriteInt(block, 8 + count * 4, value);
        WriteInt(block, 4, count + 1);
    }

    private static int ReadInt(ReadOnlySpan<byte> block, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(block[offset..]);

    private static void WriteInt(Span<byte> block, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(block[offset..], value);
}
