using System.Reflection;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Binding;

// A field bound to the property of a class that holds it: the property, the get and the set
// accessor C# sees for it on the class, how to read it from an object of the class, and how a
// value of it that is not null becomes what a record holds, checked against the declaration of
// the field at every depth. Made by ClassBinder.
//
// The accessors are those of the property found, or, for one it does not declare when it
// overrides a virtual property, the accessor of the base class it inherits: GetAccessor is public,
// SetAccessor of any accessibility. Get calls GetAccessor; a value is set through SetAccessor.
//
// ToRecord copies what is mutable: a byte array, at any depth, as a record holds its own. Take is the
// same for every field but a Binary one, whose array it takes as it is, after the same check of its
// type: the form of a write that the engine copies into its own memory while the call runs
// (EntityBinding.ValuesForWrite).
internal readonly record struct BoundProperty(
    LatticeField Field,
    PropertyInfo Property,
    MethodInfo GetAccessor,
    MethodInfo SetAccessor,
    Func<object, object?> Get,
    Func<object, object> ToRecord,
    Func<object, object> Take);
