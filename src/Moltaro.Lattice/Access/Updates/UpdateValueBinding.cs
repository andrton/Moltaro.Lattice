using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Updates;

// Binding information needed when an update takes the class form of a value.
// ClassBinder makes this with the existing property accessors and collection types.
// Null stands for a position whose class form is the same as the record form.
internal sealed record UpdateValueBinding(
    Type? ValueType = null,
    Func<object, object>? EnumToInteger = null,
    Type? NullableEnumType = null,
    BoundProperty[]? OwnedFields = null,
    UpdateValueBinding? Element = null,
    UpdateValueBinding? Key = null,
    UpdateValueBinding? Value = null,
    SequenceForm? Sequence = null,
    MapForm? Map = null);
