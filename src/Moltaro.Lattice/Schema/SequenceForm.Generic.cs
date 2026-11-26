using Moltaro.Lattice.Budgeting;

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

    public override bool IsInputType(Type type) =>
        typeof(IEnumerable<T>).IsAssignableFrom(type) && (!type.IsArray || ArrayElements.AreReadAsTheyAre(type, typeof(T)));

    public override IEnumerable<object?> Elements(object input)
    {
        foreach (var element in (IEnumerable<T>)input)
        {
            yield return element;
        }
    }

    public override object Create(int capacity = 0, MemoryLease? lease = null)
    {
        if (lease is null)
        {
            return _isSet ? new HashSet<T>(capacity, _comparer) : new List<T>(capacity);
        }

        var budget = lease.Budget;
        if (_isSet)
        {
            var set = lease.Allocate(BudgetHashSets.OwnerBytes, nameof(SequenceForm), () => new HashSet<T>(_comparer), budget.ReleaseSet);
            set.EnsureCapacity(capacity, budget, nameof(SequenceForm));
            return set;
        }

        var list = lease.Allocate(MemoryBudget.ObjectBytes(IntPtr.Size + 2L * sizeof(int)), nameof(SequenceForm),
            static () => new List<T>(), value => budget.Release(BudgetCollections.Items(value)));
        list.EnsureCapacity(capacity, budget, nameof(SequenceForm));
        return list;
    }

    public override int Count(object collection) => ((ICollection<T>)collection).Count;

    public override void Add(object collection, object? element, MemoryLease? lease = null)
    {
        var value = (T)element!;
        if (lease is not null)
        {
            if (collection is List<T> list)
            {
                list.EnsureCapacity(list.Count + 1, lease.Budget, nameof(SequenceForm));
            }
            else
            {
                var set = (HashSet<T>)collection;
                if (set.Contains(value))
                {
                    return;
                }

                set.EnsureCapacity(set.Count + 1, lease.Budget, nameof(SequenceForm));
            }
        }

        ((ICollection<T>)collection).Add(value);
    }
}
