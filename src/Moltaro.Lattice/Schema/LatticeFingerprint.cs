using System.Buffers.Binary;

namespace Moltaro.Lattice.Schema;

/// <summary>
/// A fingerprint of schema declarations: the SHA-256 digest of a canonical encoding of what
/// it covers. <see cref="LatticeSchema.Fingerprint"/> and
/// <see cref="LatticeEntityType.LayoutFingerprint"/> say what that is.
/// </summary>
/// <remarks>
/// A fingerprint is deterministic: the same declarations give the same fingerprint in every
/// process and on every platform, and a change of what it covers gives another one. It is
/// compared for equality only; it has no order.
/// </remarks>
public readonly struct LatticeFingerprint : IEquatable<LatticeFingerprint>
{
    // The 32 bytes of the digest in their order, as four big-endian parts, so that a
    // fingerprint is compared and formatted without an array.
    private readonly ulong _part0;
    private readonly ulong _part1;
    private readonly ulong _part2;
    private readonly ulong _part3;

    internal LatticeFingerprint(ReadOnlySpan<byte> digest)
    {
        _part0 = BinaryPrimitives.ReadUInt64BigEndian(digest);
        _part1 = BinaryPrimitives.ReadUInt64BigEndian(digest[8..]);
        _part2 = BinaryPrimitives.ReadUInt64BigEndian(digest[16..]);
        _part3 = BinaryPrimitives.ReadUInt64BigEndian(digest[24..]);
    }

    // The 32 bytes of the digest in their order, as the constructor takes them: for the engine,
    // which sends a fingerprint in the handshake of its replication transport.
    internal void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, _part0);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _part1);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..], _part2);
        BinaryPrimitives.WriteUInt64BigEndian(destination[24..], _part3);
    }

    /// <inheritdoc />
    public bool Equals(LatticeFingerprint other) =>
        _part0 == other._part0 && _part1 == other._part1 && _part2 == other._part2 && _part3 == other._part3;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LatticeFingerprint other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_part0, _part1, _part2, _part3);

    /// <summary>
    /// Returns the fingerprint as 64 lowercase hexadecimal digits: the 32 bytes of the
    /// digest in their order.
    /// </summary>
    public override string ToString() => $"{_part0:x16}{_part1:x16}{_part2:x16}{_part3:x16}";

    /// <summary>Tests whether two fingerprints are the same.</summary>
    public static bool operator ==(LatticeFingerprint left, LatticeFingerprint right) => left.Equals(right);

    /// <summary>Tests whether two fingerprints differ.</summary>
    public static bool operator !=(LatticeFingerprint left, LatticeFingerprint right) => !left.Equals(right);
}
