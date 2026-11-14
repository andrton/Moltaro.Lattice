using System.Text;

namespace Moltaro.Lattice.Prototypes.StringEncodingComparison;

internal sealed class Utf8Storage : ITextStorage
{
    private static readonly UTF8Encoding Encoding = new(false, true);
    private readonly byte[] _data;
    private readonly TextSlice[] _slices;
    private readonly byte[] _needle;

    public Utf8Storage(string[] values, string needle)
    {
        _slices = new TextSlice[values.Length];
        var totalBytes = 0;
        for (var index = 0; index < values.Length; index++)
        {
            var length = Encoding.GetByteCount(values[index]);
            _slices[index] = new TextSlice(totalBytes, length);
            totalBytes = checked(totalBytes + length);
        }

        _data = new byte[totalBytes];
        for (var index = 0; index < values.Length; index++)
        {
            var slice = _slices[index];
            Encoding.GetBytes(values[index].AsSpan(), _data.AsSpan(slice.Offset, slice.Length));
        }
        _needle = Encoding.GetBytes(needle);
    }

    public long PayloadBytes => _data.LongLength;

    public string ReadString(int index)
    {
        var slice = _slices[index];
        return Encoding.GetString(_data.AsSpan(slice.Offset, slice.Length));
    }

    public int CopyChars(int index, Span<char> destination)
    {
        var slice = _slices[index];
        return Encoding.GetChars(_data.AsSpan(slice.Offset, slice.Length), destination);
    }

    public bool EqualsNeedle(int index)
    {
        var slice = _slices[index];
        return _data.AsSpan(slice.Offset, slice.Length).SequenceEqual(_needle);
    }
}
