namespace Moltaro.Lattice.Schema;

// The .NET collection that a record holds at a List or a Set position: List<T> or
// HashSet<T>, where T is what a record holds at the element position, null included where
// the element may be null. A HashSet compares its members with the comparer the schema
// declares for the element. Code that copies values without knowing T makes, reads and
// fills such a collection through this class.
//
// One is made for every List and Set declaration when the schema is built, so the generic
// type is resolved once there and never when a value is copied.
internal abstract class SequenceForm
{
    // List<T> or HashSet<T>.
    public abstract Type ValueType { get; }

    // Whether a value is what a record takes for the position: a sequence of exactly T.
    public abstract bool IsInput(object value);

    // The elements of an input that IsInput accepted, in its order.
    public abstract IEnumerable<object?> Elements(object input);

    // A new, empty collection of the record.
    public abstract object Create();

    // Adds an element of type T. A HashSet keeps the member it has when an equal one is
    // added, so of equal members the first is kept.
    public abstract void Add(object collection, object? element);

    public static SequenceForm For(bool isSet, LatticeValueDeclaration element)
    {
        var formType = typeof(SequenceForm<>).MakeGenericType(element.PositionType);
        return (SequenceForm)Activator.CreateInstance(formType, isSet, element.TextComparer)!;
    }
}
