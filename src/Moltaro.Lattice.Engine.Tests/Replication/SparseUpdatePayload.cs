using System.Buffers.Binary;

namespace Moltaro.Lattice.Engine.Tests.Replication;

// The sparse effect envelope of wire format 7, independent of its engine codec.
// A test replaces the declared String key or predecessor to exercise replay.
internal static class SparseUpdatePayload
{
    public const int BaseSequenceAt = 1 + sizeof(ulong) + 2 * sizeof(uint);

    private const int KeyLengthAt = BaseSequenceAt + sizeof(ulong);

    public static byte[] WithStringKey(byte[] effect, ReadOnlySpan<byte> encodedKey)
    {
        var oldLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(effect.AsSpan(KeyLengthAt)));
        var delta = encodedKey.Length - oldLength;
        var newEffect = new byte[effect.Length + delta];
        var keyAt = KeyLengthAt + sizeof(uint);
        effect.AsSpan(0, keyAt).CopyTo(newEffect);

        var bodyLength = BinaryPrimitives.ReadInt64LittleEndian(effect.AsSpan(1));
        BinaryPrimitives.WriteInt64LittleEndian(newEffect.AsSpan(1), bodyLength + delta);
        BinaryPrimitives.WriteUInt32LittleEndian(newEffect.AsSpan(KeyLengthAt), (uint)encodedKey.Length);
        encodedKey.CopyTo(newEffect.AsSpan(keyAt));
        effect.AsSpan(keyAt + oldLength).CopyTo(newEffect.AsSpan(keyAt + encodedKey.Length));
        return newEffect;
    }
}
