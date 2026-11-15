namespace Moltaro.Lattice.Schema;

// The one rule by which an array is taken where an array of a declared element type is
// expected: for a List or a Set of a record, see SequenceForm, and for an array property of a
// bound class at any depth, see ClassBinder.
//
// The runtime lets an array stand for an array of another element type in two ways:
//   - For reference types, by covariance: an array of a class derived from T stands for a T[],
//     and every element of it really is a T, so it is read as it is. Such an array is taken.
//   - For value types of one size, the integers of one width and an enum and its underlying
//     type, by reinterpretation: a uint[] stands for an int[], an sbyte[] for a byte[], and
//     reading an element reads its bits as another value. Such an array is refused.
// An array of arrays carries either way inward, an sbyte[][] stands for a byte[][], so the two
// element types are compared after every level of array both have is taken off.
internal static class ArrayElements
{
    // Whether the elements of an array that the runtime takes for an array of the declared
    // element type are read as what they are: its element type is the declared one, or a
    // reference type, which stands for the declared one by covariance only.
    public static bool AreReadAsTheyAre(Type arrayType, Type declaredElementType)
    {
        var element = arrayType.GetElementType()!;
        var declared = declaredElementType;
        while (element.IsArray && declared.IsArray)
        {
            element = element.GetElementType()!;
            declared = declared.GetElementType()!;
        }

        return element == declared || !element.IsValueType;
    }
}
