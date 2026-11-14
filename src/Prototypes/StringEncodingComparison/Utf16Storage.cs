namespace Moltaro.Lattice.Prototypes.StringEncodingComparison;

internal sealed class Utf16Storage : ITextStorage
{
    private readonly char[] _data;
    private readonly TextSlice[] _slices;
    private readonly string _needle;

    public Utf16Storage(string[] values, string needle)
    {
        _slices = new TextSlice[values.Length];
        var totalCharacters = 0;
        for (var index = 0; index < values.Length; index++)
        {
            _slices[index] = new TextSlice(totalCharacters, values[index].Length);
            totalCharacters = checked(totalCharacters + values[index].Length);
        }

        _data = new char[totalCharacters];
        for (var index = 0; index < values.Length; index++)
        {
            var slice = _slices[index];
            values[index].AsSpan().CopyTo(_data.AsSpan(slice.Offset, slice.Length));
        }
        _needle = needle;
    }

    public long PayloadBytes => _data.LongLength * sizeof(char);

    public string ReadString(int index)
    {
        var slice = _slices[index];
        return new string(_data.AsSpan(slice.Offset, slice.Length));
    }

    public int CopyChars(int index, Span<char> destination)
    {
        var slice = _slices[index];
        _data.AsSpan(slice.Offset, slice.Length).CopyTo(destination);
        return slice.Length;
    }

    public bool EqualsNeedle(int index)
    {
        var slice = _slices[index];
        return _data.AsSpan(slice.Offset, slice.Length).SequenceEqual(_needle.AsSpan());
    }
}
