namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

// A bounded interoperability probe, not a production string format. The owner
// keeps valid UTF-8 bytes alive and unmoved for every operation using this view.
internal readonly unsafe struct NativeTextKey : IEquatable<NativeTextKey>
{
    public const int MaximumProbeBytes = 1024;
    private readonly nint _address;
    private readonly int _length;
    private readonly StringComparison _comparison;

    public NativeTextKey(nint address, int length, StringComparison comparison)
    {
        _address = address;
        _length = length;
        _comparison = comparison;
    }

    private ReadOnlySpan<byte> Bytes => new((void*)_address, _length);

    public bool Equals(NativeTextKey other)
    {
        if (_comparison != other._comparison)
        {
            return false;
        }

        if (_comparison == StringComparison.Ordinal)
        {
            return Bytes.SequenceEqual(other.Bytes);
        }

        // Decode into bounded stack scratch to use the runtime's exact Unicode
        // OrdinalIgnoreCase behavior. Do not approximate it by lowercasing bytes.
        Span<char> left = stackalloc char[MaximumProbeBytes];
        Span<char> right = stackalloc char[MaximumProbeBytes];
        var leftLength = NativeTextBuffer.Encoding.GetChars(Bytes, left);
        var rightLength = NativeTextBuffer.Encoding.GetChars(other.Bytes, right);
        return left[..leftLength].Equals(right[..rightLength], StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => obj is NativeTextKey other && Equals(other);

    public override int GetHashCode()
    {
        if (_comparison == StringComparison.Ordinal)
        {
            var hash = new HashCode();
            hash.AddBytes(Bytes);
            return hash.ToHashCode();
        }

        Span<char> text = stackalloc char[MaximumProbeBytes];
        var length = NativeTextBuffer.Encoding.GetChars(Bytes, text);
        return string.GetHashCode(text[..length], StringComparison.OrdinalIgnoreCase);
    }
}
