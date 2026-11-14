namespace Moltaro.Lattice.Schema;

// See SequenceForm. T is the type of the element position.
internal sealed class SequenceForm<T> : SequenceForm
{
    private readonly bool _isSet;
    private readonly IEqualityComparer<T> _comparer;

    // The text comparer is the comparison of a String element, or of the key of the target of
    // a Reference element; any other element compares as .NET compares T by default.
    public SequenceForm(bool isSet, IEqualityComparer<string>? textComparer)
    {
        _isSet = isSet;
        _comparer = textComparer as IEqualityComparer<T> ?? EqualityComparer<T>.Default;
    }

    public override Type ValueType => _isSet ? typeof(HashSet<T>) : typeof(List<T>);

    // The runtime takes an array of one integer type for an array of another of the same size,
    // and an array of an enum for one of its underlying type: a uint[] passes as an
    // IEnumerable<int>, and its values would be read as other values. So an array is taken
    // only when its element type is exactly T. Any other sequence of T yields values of T.
    public override bool IsInput(object value) =>
        value is IEnumerable<T> && (value is not Array || value.GetType() == typeof(T[]));

    public override IEnumerable<object?> Elements(object input)
    {
        foreach (var element in (IEnumerable<T>)input)
        {
            yield return element;
        }
    }

    public override object Create() => _isSet ? new HashSet<T>(_comparer) : new List<T>();

    public override void Add(object collection, object? element) => ((ICollection<T>)collection).Add((T)element!);
}
