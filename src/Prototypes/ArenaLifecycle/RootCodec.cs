using System.Buffers.Binary;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal static class RootCodec
{
    public const int Bytes = 128;
    public const int ChunkBytes = 256;
    public const int MaximumChunks = 8;

    public static int Int(Span<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    public static long Long(Span<byte> bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]);
    public static void Int(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);
    public static void Long(Span<byte> bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes[offset..], value);

    public static BlockAddress Chunk(Span<byte> root, int ordinal) =>
        new(Int(root, 64 + ordinal * 8), Int(root, 68 + ordinal * 8));

    public static void Chunk(Span<byte> root, int ordinal, BlockAddress address)
    {
        Int(root, 64 + ordinal * 8, address.Segment);
        Int(root, 68 + ordinal * 8, address.Offset);
    }

    public static TextToken Text(Span<byte> root) => new(Int(root, 40), Int(root, 44));
    public static byte Pattern(int seed, int index) => (byte)((seed * 31 + index * 17) % 251);

    public static void Initialize(Span<byte> root, ValueSpec value, TextToken text, BlockAddress[] chunks)
    {
        root.Clear();
        Int(root, 0, value.Key);
        Int(root, 4, value.Seed);
        Long(root, 8, value.Number);
        Long(root, 16, ~value.Number);
        Int(root, 32, value.Length);
        Int(root, 36, chunks.Length);
        Int(root, 40, text.Entry);
        Int(root, 44, text.Generation);
        for (var index = 0; index < chunks.Length; index++)
        {
            Chunk(root, index, chunks[index]);
        }
    }
}
