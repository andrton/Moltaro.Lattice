using Moltaro.Lattice.Budgeting;

namespace Moltaro.Lattice.Schema;

// The .NET collection that a record holds at a Map position: Dictionary<K, V>, where K and V
// are what a record holds at the key and the value position, null included where the value
// may be null, with the comparer the schema declares for the key. Code that copies values
// without knowing K and V makes, reads and fills such a collection through this class.
//
// One is made for every Map declaration when the schema is built, so the generic type is
// resolved once there and never when a value is copied.
internal abstract class MapForm
{
    // Dictionary<K, V>.
    public abstract Type ValueType { get; }

    // Whether a value is what a record takes for the position: a sequence of key-value pairs
    // of exactly K and V.
    public abstract bool IsInput(object value);

    public abstract bool IsInputType(Type type);

    // The entries of an input that IsInput accepted, in its order. A key may be null in an
    // input.
    public abstract IEnumerable<KeyValuePair<object?, object?>> Entries(object input);

    // A new, empty collection of the record.
    public abstract object Create(int capacity = 0, MemoryLease? lease = null);

    // Adds an entry whose key and value are of type K and V; false when the Map already has a
    // key that the key declaration holds equal.
    public abstract bool TryAdd(object map, object key, object? value, MemoryLease? lease = null);

    public static MapForm For(LatticeValueDeclaration key, LatticeValueDeclaration value) =>
        For(key.PositionType, value.PositionType, key.TextComparison);

    internal static MapForm For(Type key, Type value, TextComparison? comparison)
    {
        var formType = typeof(MapForm<,>).MakeGenericType(key, value);
        return (MapForm)Activator.CreateInstance(formType, comparison)!;
    }
}
