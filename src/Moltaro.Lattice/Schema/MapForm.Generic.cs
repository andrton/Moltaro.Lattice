namespace Moltaro.Lattice.Schema;

// See MapForm. TKey and TValue are the types of the key and the value position; a Map key is
// never nullable.
internal sealed class MapForm<TKey, TValue> : MapForm
    where TKey : notnull
{
    private readonly IEqualityComparer<TKey> _comparer;

    // The text comparer is the comparison of a String key, or of the key of the target of a
    // Reference key; any other key compares as .NET compares TKey by default.
    public MapForm(IEqualityComparer<string>? textComparer)
    {
        _comparer = textComparer as IEqualityComparer<TKey> ?? EqualityComparer<TKey>.Default;
    }

    public override Type ValueType => typeof(Dictionary<TKey, TValue>);

    public override bool IsInput(object value) => value is IEnumerable<KeyValuePair<TKey, TValue>>;

    public override IEnumerable<KeyValuePair<object?, object?>> Entries(object input)
    {
        foreach (var (key, value) in (IEnumerable<KeyValuePair<TKey, TValue>>)input)
        {
            yield return new(key, value);
        }
    }

    public override object Create() => new Dictionary<TKey, TValue>(_comparer);

    public override bool TryAdd(object map, object key, object? value) =>
        ((Dictionary<TKey, TValue>)map).TryAdd((TKey)key, (TValue)value!);
}
