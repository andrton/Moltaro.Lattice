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

    // An array that passes as an IEnumerable<T> is taken by the rule of ArrayElements: an array
    // of a class derived from T, whose elements are T, is; a uint[] passing for an int[], whose
    // elements would be read as other values, is not. Any other sequence of T yields values of
    // T.
    public override bool IsInput(object value) =>
        value is IEnumerable<T> && (value is not Array array || ArrayElements.AreReadAsTheyAre(array.GetType(), typeof(T)));

    public override IEnumerable<object?> Elements(object input)
    {
        foreach (var element in (IEnumerable<T>)input)
        {
            yield return element;
        }
    }

    public override object Create(int capacity = 0) => _isSet ? new HashSet<T>(capacity, _comparer) : new List<T>(capacity);

    public override int Count(object collection) => ((ICollection<T>)collection).Count;

    public override void Add(object collection, object? element) => ((ICollection<T>)collection).Add((T)element!);
}
