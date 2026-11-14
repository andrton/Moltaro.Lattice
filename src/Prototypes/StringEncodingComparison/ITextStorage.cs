namespace Moltaro.Lattice.Prototypes.StringEncodingComparison;

internal interface ITextStorage
{
    long PayloadBytes { get; }
    string ReadString(int index);
    int CopyChars(int index, Span<char> destination);
    bool EqualsNeedle(int index);
}
