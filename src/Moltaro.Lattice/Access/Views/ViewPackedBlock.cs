namespace Moltaro.Lattice.Access.Views;

// A block of packed List records borrowed in the current read epoch. Stride zero
// leaves the enumerator on the ordinary entry path.
internal readonly ref struct ViewPackedBlock(ReadOnlySpan<byte> bytes, int stride)
{
    public ReadOnlySpan<byte> Bytes { get; } = bytes;

    public int Stride { get; } = stride;
}
