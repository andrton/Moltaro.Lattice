using Moltaro.Lattice.Budgeting;

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

    // Unlike an array of integers, an array of key-value pairs passes only as itself: the
    // runtime takes an array of one struct for an array of another only for integers and
    // enums, and KeyValuePair<TKey, TValue> is neither.
    public override bool IsInput(object value) => value is IEnumerable<KeyValuePair<TKey, TValue>>;

    public override bool IsInputType(Type type) => typeof(IEnumerable<KeyValuePair<TKey, TValue>>).IsAssignableFrom(type);

    public override IEnumerable<KeyValuePair<object?, object?>> Entries(object input)
    {
        foreach (var (key, value) in (IEnumerable<KeyValuePair<TKey, TValue>>)input)
        {
            yield return new(key, value);
        }
    }

    public override object Create(int capacity = 0, MemoryLease? lease = null)
    {
        if (lease is null)
        {
            return new Dictionary<TKey, TValue>(capacity, _comparer);
        }

        var budget = lease.Budget;
        var map = lease.Allocate(BudgetDictionaries.OwnerBytes, nameof(MapForm), () => new Dictionary<TKey, TValue>(_comparer),
            value => budget.ReleaseDictionary(value));
        map.EnsureCapacity(capacity, budget, nameof(MapForm));
        return map;
    }

    public override bool TryAdd(object map, object key, object? value, MemoryLease? lease = null)
    {
        var dictionary = (Dictionary<TKey, TValue>)map;
        if (lease is not null)
        {
            if (dictionary.ContainsKey((TKey)key))
            {
                return false;
            }

            dictionary.EnsureCapacity(dictionary.Count + 1, lease.Budget, nameof(MapForm));
        }

        return dictionary.TryAdd((TKey)key, (TValue)value!);
    }
}
